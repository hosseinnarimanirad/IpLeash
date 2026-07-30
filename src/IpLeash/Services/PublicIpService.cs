using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace IpLeash.Services;

/// <summary>
/// Asks a short list of plain-text echo services for the machine's public IP, taking the first
/// answer that parses. Multiple providers keep a single service outage from causing a spurious
/// fail-closed block.
/// </summary>
public sealed class PublicIpService : IPublicIpService, IDisposable
{
    private static readonly string[] Providers =
    [
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "https://icanhazip.com",
    ];

    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;

    public PublicIpService()
    {
        var handler = new SocketsHttpHandler
        {
            // Without this, a pooled connection survives a VPN flip and keeps reporting the old
            // exit IP — exactly the case this app exists to catch.
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("IpLeash/1.0");
    }

    public async Task<IPAddress?> GetPublicIpAsync(CancellationToken ct = default)
    {
        foreach (var provider in Providers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(PerRequestTimeout);

                var body = await _http.GetStringAsync(provider, timeout.Token).ConfigureAwait(false);
                if (IPAddress.TryParse(body.Trim(), out var address))
                {
                    return address;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Provider unreachable, timed out, or returned junk — try the next one.
            }
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
