using System.Diagnostics;

namespace QxTui.Core;

/// <summary>Real desktop notifications (freedesktop / macOS) for apps that
/// want the terminal-independent kind: a long git op finishes while you
/// are in another window and the OS tells you. Fire-and-forget on a pool
/// thread, never throws, never blocks the frame loop; missing notifier
/// binaries are silently ignored (a TUI must survive headless boxes).
///
/// Opt-in everywhere: Toast(desktop: true) per notification, or
/// App.DesktopNotifications for every toast in the app.</summary>
public static class DesktopNotify
{
    /// <summary>Test hook: when set, Send() calls this instead of spawning
    /// a process — assertions without a desktop session.</summary>
    internal static Action<string, string>? TestSink;

    private static int _inFlight;
    private static string? _cachedCmd;
    private static bool _probed;

    /// <summary>Queue a desktop notification. Coalesced: while one spawn
    /// is in flight further sends are dropped rather than piled up — a
    /// toast storm must not become a process storm.</summary>
    public static void Send(string title, string body)
    {
        if (TestSink is { } sink)
        {
            sink(title, body);
            return;
        }
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var (cmd, args) = CommandFor(title, body);
                if (cmd is not null) Run(cmd, args);
            }
            catch
            {
                // no notifier, no session, no problem: the in-TUI toast
                // already told the user who was watching
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        });
    }

    private static (string? Cmd, List<string> Args) CommandFor(string title, string body)
    {
        if (OperatingSystem.IsLinux())
            return (Probe("notify-send"),
                ["--app-name", "qxtui", "--", title, body]);
        if (OperatingSystem.IsMacOS())
            return (Probe("osascript"),
                ["-e", $"display notification \"{Esc(body)}\" with title \"{Esc(title)}\""]);
        return (null, []);
    }

    /// <summary>which(1)-style probe, cached: a headless CI box resolves
    /// once and never spawns again.</summary>
    private static string? Probe(string tool)
    {
        if (_probed) return _cachedCmd;
        _probed = true;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, tool);
            if (File.Exists(candidate))
            {
                _cachedCmd = candidate;
                break;
            }
        }
        return _cachedCmd;
    }

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void Run(string cmd, List<string> args)
    {
        var psi = new ProcessStartInfo(cmd)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        p?.WaitForExit(2000); // bounded: a hung notifier must not leak
    }
}
