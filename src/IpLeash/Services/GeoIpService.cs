using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using IpLeash.Models;

namespace IpLeash.Services;

/// <summary>
/// Asks a short list of JSON geolocation services which country an address belongs to, taking
/// the first answer that parses, and remembers it.
///
/// Shaped after <see cref="PublicIpService"/>, with three differences that matter:
///
/// Every provider is HTTPS. ip-api.com is the obvious fourth candidate and is deliberately
/// absent: its free tier is plaintext HTTP, and in country mode a forged response is a forged
/// <em>allow</em> — the one direction a kill switch must never fail in.
///
/// Every provider takes the address in the path, so this answers "which country is this
/// address in", not "where am I". That makes answers cacheable per address and reusable for the
/// saved chips, and removes any chance of a second opinion about the current IP disagreeing
/// with <see cref="PublicIpService"/>.
///
/// Answers are cached for 30 days, so in the steady state no request is made at all — the
/// address only changes when the VPN does, which is exactly when a fresh answer is wanted.
/// </summary>
public sealed class GeoIpService : IGeoIpService, IDisposable
{
    private delegate CountryInfo? ResponseParser(JsonElement root);

    private sealed record Provider(string Name, string UrlFormat, ResponseParser Parse);

    private static readonly Provider[] Providers =
    [
        new("ipwho.is", "https://ipwho.is/{0}", ParseIpWhoIs),
        new("ipapi.co", "https://ipapi.co/{0}/json/", ParseIpApiCo),
        new("api.country.is", "https://api.country.is/{0}", ParseCountryIs),
    ];

    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Caps the whole chain, so a country lookup can never stall an evaluation tick.</summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(30);

    /// <summary>
    /// Failures are remembered only briefly and never written to disk. Persisting "unknown"
    /// would turn a transient provider outage into a fail-closed block that survives a restart.
    /// </summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ProviderCooldown = TimeSpan.FromMinutes(10);

    /// <summary>Spacing between network calls, so a burst of chips cannot trip a rate limit.</summary>
    private static readonly TimeSpan MinNetworkInterval = TimeSpan.FromMilliseconds(1200);

    private const int MaxCacheEntries = 512;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(5);

    private readonly IGeoCacheStore _store;
    private readonly HttpClient _http;

    private readonly ConcurrentDictionary<string, GeoCacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _negativeCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.Ordinal);

    /// <summary>Collapses concurrent asks for the same address into one request.</summary>
    private readonly ConcurrentDictionary<string, Task<CountryInfo?>> _inFlight = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private DateTimeOffset _lastNetworkCall = DateTimeOffset.MinValue;

    private readonly object _saveLock = new();
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private bool _dirty;
    private bool _disposed;

    public GeoIpService(IGeoCacheStore store)
    {
        _store = store;

        var handler = new SocketsHttpHandler
        {
            // Same reasoning as PublicIpService: a pooled connection that survives a VPN flip
            // would keep answering for the old exit.
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("IpLeash/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        foreach (var entry in _store.Load())
        {
            if (DateTimeOffset.Now - entry.ResolvedAt < PositiveTtl)
            {
                _cache[entry.Ip] = entry;
            }
        }
    }

    public CountryInfo? TryGetCached(IPAddress address)
    {
        if (!IpAddressClassifier.IsPubliclyRoutable(address))
        {
            return null;
        }

        var key = IpAddressClassifier.CacheKey(address);

        if (!_cache.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (DateTimeOffset.Now - entry.ResolvedAt >= PositiveTtl)
        {
            _cache.TryRemove(key, out _);
            return null;
        }

        return new CountryInfo(entry.Code, entry.Name, entry.City);
    }

    public Task<CountryInfo?> GetCountryAsync(IPAddress address, CancellationToken ct = default)
    {
        // Screened before anything else: a private or reserved address has no country to look up,
        // and asking would invite a provider to answer about the caller instead.
        if (!IpAddressClassifier.IsPubliclyRoutable(address))
        {
            return Task.FromResult<CountryInfo?>(null);
        }

        var key = IpAddressClassifier.CacheKey(address);

        if (TryGetCached(address) is { } cached)
        {
            return Task.FromResult<CountryInfo?>(cached);
        }

        if (_negativeCache.TryGetValue(key, out var failedAt))
        {
            if (DateTimeOffset.Now - failedAt < NegativeTtl)
            {
                return Task.FromResult<CountryInfo?>(null);
            }

            _negativeCache.TryRemove(key, out _);
        }

        // One request per address no matter how many callers arrive at once. The shared task
        // deliberately does not observe any single caller's token — a chip giving up must not
        // cancel the lookup the engine is waiting on.
        var lookup = _inFlight.GetOrAdd(key, k => LookupAndCacheAsync(k));

        return AwaitWithCallerCancellation(lookup, ct);
    }

    private static async Task<CountryInfo?> AwaitWithCallerCancellation(
        Task<CountryInfo?> lookup, CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
        {
            return await lookup.ConfigureAwait(false);
        }

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => cancelled.TrySetResult());

        var finished = await Task.WhenAny(lookup, cancelled.Task).ConfigureAwait(false);
        if (finished != lookup)
        {
            ct.ThrowIfCancellationRequested();
        }

        return await lookup.ConfigureAwait(false);
    }

    private async Task<CountryInfo?> LookupAndCacheAsync(string key)
    {
        try
        {
            var result = await QueryProvidersAsync(key).ConfigureAwait(false);

            if (result is null)
            {
                _negativeCache[key] = DateTimeOffset.Now;
                return null;
            }

            _cache[key] = new GeoCacheEntry(key, result.Code, result.Name, result.City, DateTimeOffset.Now);
            _negativeCache.TryRemove(key, out _);
            MarkDirtyAndMaybeSave();

            return result;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<CountryInfo?> QueryProvidersAsync(string ip)
    {
        using var budget = new CancellationTokenSource(TotalBudget);

        foreach (var provider in Providers)
        {
            if (budget.IsCancellationRequested)
            {
                break;
            }

            if (_cooldowns.TryGetValue(provider.Name, out var until))
            {
                if (DateTimeOffset.Now < until)
                {
                    continue;
                }

                _cooldowns.TryRemove(provider.Name, out _);
            }

            try
            {
                var result = await QueryOneAsync(provider, ip, budget.Token).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                // Out of time for the whole chain.
                break;
            }
            catch
            {
                // Unreachable, timed out, or returned junk — try the next provider.
            }
        }

        return null;
    }

    private async Task<CountryInfo?> QueryOneAsync(Provider provider, string ip, CancellationToken ct)
    {
        await ThrottleAsync(ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerRequestTimeout);

        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, provider.UrlFormat, ip);

        using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            ApplyCooldown(provider, response);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Both ipwho.is and ipapi.co report quota and lookup errors inside a 200 response, so the
        // status code alone is not evidence of an answer.
        if (IsErrorPayload(root))
        {
            ApplyCooldown(provider, response);
            return null;
        }

        return provider.Parse(root);
    }

    private static bool IsErrorPayload(JsonElement root)
    {
        if (root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            return true;
        }

        return root.TryGetProperty("error", out var error)
               && error.ValueKind is JsonValueKind.True or JsonValueKind.String or JsonValueKind.Object;
    }

    private void ApplyCooldown(Provider provider, HttpResponseMessage response)
    {
        var cooldown = ProviderCooldown;

        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            cooldown = delta;
        }
        else if (response.Headers.TryGetValues("X-Ttl", out var values)
                 && int.TryParse(values.FirstOrDefault(), out var seconds) && seconds > 0)
        {
            cooldown = TimeSpan.FromSeconds(seconds);
        }

        _cooldowns[provider.Name] = DateTimeOffset.Now + cooldown;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _networkGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var since = DateTimeOffset.Now - _lastNetworkCall;
            if (since < MinNetworkInterval)
            {
                await Task.Delay(MinNetworkInterval - since, ct).ConfigureAwait(false);
            }

            _lastNetworkCall = DateTimeOffset.Now;
        }
        finally
        {
            _networkGate.Release();
        }
    }

    // ---- Provider response shapes ------------------------------------------------------------

    private static CountryInfo? ParseIpWhoIs(JsonElement root) => Build(
        ReadString(root, "country_code"),
        ReadString(root, "country"),
        ReadString(root, "city"));

    private static CountryInfo? ParseIpApiCo(JsonElement root) => Build(
        ReadString(root, "country_code"),
        ReadString(root, "country_name"),
        ReadString(root, "city"));

    private static CountryInfo? ParseCountryIs(JsonElement root) => Build(
        ReadString(root, "country"),
        name: null,
        city: null);

    private static CountryInfo? Build(string? code, string? name, string? city)
    {
        // An empty or malformed code is a failed lookup, never a match against an empty expected
        // value. This is the guard that keeps a garbled response from reading as "allowed".
        var normalized = CountryInfo.NormalizeCode(code);
        if (normalized is null)
        {
            return null;
        }

        var display = string.IsNullOrWhiteSpace(name) ? CountryCatalog.NameOf(normalized) : name.Trim();
        return new CountryInfo(normalized, display, string.IsNullOrWhiteSpace(city) ? null : city.Trim());
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ---- Persistence -------------------------------------------------------------------------

    private void MarkDirtyAndMaybeSave()
    {
        lock (_saveLock)
        {
            _dirty = true;

            if (_disposed || DateTimeOffset.Now - _lastSave < SaveDebounce)
            {
                return;
            }

            SaveCoreLocked();
        }
    }

    private void SaveCoreLocked()
    {
        if (!_dirty)
        {
            return;
        }

        var entries = _cache.Values
            .OrderByDescending(e => e.ResolvedAt)
            .Take(MaxCacheEntries)
            .ToList();

        _store.Save(entries);
        _lastSave = DateTimeOffset.Now;
        _dirty = false;
    }

    public void Dispose()
    {
        lock (_saveLock)
        {
            if (_disposed)
            {
                return;
            }

            // Flush whatever the debounce is still holding, so a short session still leaves a
            // usable cache behind.
            SaveCoreLocked();
            _disposed = true;
        }

        _http.Dispose();
        _networkGate.Dispose();
    }
}
