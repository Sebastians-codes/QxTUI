namespace QxTui.Core;

/// <summary>Debounced filesystem watcher: coalesces FileSystemWatcher's
/// event flood (one callback per file, pool thread) into a SINGLE message
/// posted to the app after <paramref name="quietMs"/> of silence. Apps
/// stay live without polling or restarting — qxgit refreshes status/log
/// when the repository changes under it, from any process.
///
/// Thread-safe by construction: the only app touch is Post (the supported
/// cross-thread ingress). Dispose stops watcher and timer.</summary>
public sealed class FileWatcher : IDisposable
{
    private readonly App _app;
    private readonly Func<object> _message;
    private readonly int _quietMs;
    private readonly FileSystemWatcher _watcher;
    private readonly Func<string, bool>? _include;
    private readonly object _gate = new();
    private Timer? _timer;
    private int _disposed;

    /// <param name="include">Optional event filter on the changed path.
    /// Repositories need this: `git status` rewrites .git/index, which
    /// would re-trigger the watcher that triggered the status — a
    /// perpetual refresh loop. Ignore .git except HEAD/refs.</param>
    public FileWatcher(App app, string path, Func<object> message, int quietMs = 300,
        Func<string, bool>? include = null)
    {
        _app = app;
        _message = message;
        _quietMs = quietMs;
        _include = include;
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true, // .git lives inside: commits from
                                          // other processes are changes too
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnFs;
        _watcher.Created += OnFs;
        _watcher.Deleted += OnFs;
        _watcher.Renamed += OnFs;
        // internal buffer overflow means "something changed, details
        // lost" — that is still a refresh signal
        _watcher.Error += (_, _) => Arm();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>The watched path (repositories, config dirs, log dirs).</summary>
    public string Path => _watcher.Path;

    private void OnFs(object sender, FileSystemEventArgs e)
    {
        if (_include is { } f && !f(e.FullPath)) return;
        Arm();
    }

    private void Arm()
    {
        lock (_gate)
        {
            if (_disposed == 1) return;
            _timer?.Dispose();
            _timer = new Timer(_ =>
            {
                if (_disposed == 0)
                    _app.Post(_message());
            }, null, _quietMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        _watcher.Dispose();
    }
}
