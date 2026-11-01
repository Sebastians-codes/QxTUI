namespace QxTui.Terminal;

/// <summary>Picks the terminal implementation for the current OS, so one
/// build of an app runs on Linux, macOS and Windows.</summary>
public static class TerminalFactory
{
    /// <exception cref="PlatformNotSupportedException">The OS has no
    /// terminal implementation.</exception>
    public static ITerminal Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsTerminal();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return new PosixTerminal();
        throw new PlatformNotSupportedException("qxtui runs on Linux, macOS and Windows.");
    }
}
