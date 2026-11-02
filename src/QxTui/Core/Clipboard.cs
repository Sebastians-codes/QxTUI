using System.Diagnostics;
using System.Text;

namespace QxTui.Core;

/// <summary>
/// Copies text to the system clipboard. Sends an OSC 52 escape (works over SSH
/// and in most modern terminals) and, where a clipboard tool is installed, also
/// hands the text to it for terminals that ignore OSC 52: <c>wl-copy</c> /
/// <c>xclip</c> / <c>xsel</c> on Linux, <c>pbcopy</c> on macOS, PowerShell's
/// <c>Set-Clipboard</c> on Windows. Best effort: it never throws.
/// </summary>
public static class Clipboard
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(3);

    internal enum Platform { Linux, MacOS, Windows }

    /// <summary>Copies <paramref name="text"/>. Safe from any thread.</summary>
    public static void Copy(string text)
    {
        SendOsc52(text);
        _ = Task.Run(() => RunTool(text));
    }

    /// <summary>The OSC 52 "set clipboard" sequence for <paramref name="text"/>.</summary>
    internal static byte[] Osc52(string text) =>
        Encoding.ASCII.GetBytes($"\u001b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\u0007");

    /// <summary>Which clipboard tool to run, if any. Pure, so every platform is testable anywhere.</summary>
    internal static (string Command, string[] Arguments)? ChooseTool(
        Platform platform, Func<string, string?> env, Func<string, bool> onPath)
    {
        switch (platform)
        {
            case Platform.Windows:
                // clip.exe mangles non-ASCII (system code page), so go through PowerShell as UTF-8
                return ("powershell",
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::InputEncoding = [Text.Encoding]::UTF8; Set-Clipboard -Value ([Console]::In.ReadToEnd())",
                ]);
            case Platform.MacOS:
                return onPath("pbcopy") ? ("pbcopy", []) : null;
            default:
                if (!string.IsNullOrEmpty(env("WAYLAND_DISPLAY")) && onPath("wl-copy"))
                    return ("wl-copy", []);
                if (!string.IsNullOrEmpty(env("DISPLAY")))
                {
                    if (onPath("xclip")) return ("xclip", ["-selection", "clipboard"]);
                    if (onPath("xsel")) return ("xsel", ["--clipboard", "--input"]);
                }
                return null;
        }
    }

    private static void SendOsc52(string text)
    {
        try
        {
            var sequence = Osc52(text);
            if (OperatingSystem.IsWindows())
            {
                // no /dev/tty: the raw stdout handle (unaffected by Console.SetOut)
                var stdout = Console.OpenStandardOutput();
                stdout.Write(sequence);
                stdout.Flush();
                return;
            }

            // A separate unbuffered handle: one write(2) cannot interleave inside
            // a frame the renderer is writing through its own stream.
            using var tty = new FileStream("/dev/tty", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
            tty.Write(sequence);
        }
        catch (Exception)
        {
            // no controlling terminal: the tool fallback may still work
        }
    }

    private static void RunTool(string text)
    {
        try
        {
            var platform = OperatingSystem.IsWindows() ? Platform.Windows
                : OperatingSystem.IsMacOS() ? Platform.MacOS
                : Platform.Linux;
            if (ChooseTool(platform, Environment.GetEnvironmentVariable, OnPath) is not var (command, arguments))
                return;

            var info = new ProcessStartInfo(command)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null)
                return;
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(ToolTimeout))
                process.Kill();
        }
        catch (Exception)
        {
            // tool missing or hung: OSC 52 already went out
        }
    }

    private static bool OnPath(string command) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? command + ".exe" : command)));
}
