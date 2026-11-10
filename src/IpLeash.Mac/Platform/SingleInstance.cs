using System.IO;
using System.Net.Sockets;
using IpLeash.Services;

namespace IpLeash.Platform;

/// <summary>
/// Ensures one IpLeash per user, and lets a second launch surface the first instead.
///
/// Not a nicety, for the same reason as on Windows: a second instance's startup recovery would
/// resume the processes the first one has frozen, silently unblocking apps while the first
/// window still reads BLOCKED. Launch Services already refuses a second copy of the .app, but
/// running the binary directly bypasses that, so the guard lives here.
///
/// An exclusive lock on a file decides who runs (the kernel drops it if the holder dies, so a
/// crash never leaves the app unlaunchable), and a Unix socket beside it carries the "show
/// yourself" request.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly string _lockPath = Path.Combine(AppPaths.DataDirectory, "instance.lock");
    private readonly string _socketPath = Path.Combine(AppPaths.DataDirectory, "instance.sock");

    private FileStream? _lock;
    private Socket? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on a thread-pool thread when another launch asks this instance to show itself.</summary>
    public event EventHandler? ShowRequested;

    public bool TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);

            // FileShare.None is enforced on Unix with an advisory flock, which is all this needs.
            _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        StartListening();
        return true;
    }

    /// <summary>Asks the running instance to show its window. Returns false if it could not be reached.</summary>
    public bool SignalRunningInstance()
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(_socketPath));
            client.Send("show"u8);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void StartListening()
    {
        try
        {
            // A leftover from a crashed run; we hold the lock, so nothing else owns it.
            File.Delete(_socketPath);

            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _listener.Listen(4);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            // Without this the app still works; a second launch just cannot surface it.
            _listener?.Dispose();
            _listener = null;
            return;
        }

        _cts = new CancellationTokenSource();
        var listener = _listener;
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var connection = await listener.AcceptAsync(token).ConfigureAwait(false);
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // One bad connection must not end the listener.
                }
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _cts?.Dispose();

        if (_lock is not null)
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (IOException)
            {
            }

            _lock.Dispose();
            _lock = null;
        }
    }
}
