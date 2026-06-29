using System.Runtime.InteropServices;

namespace DupeHunter;

/// <summary>
/// Shared helper for the in-place progress displays (<see cref="MultiProgress"/>,
/// <see cref="StepProgress"/>): enables ANSI escape handling so cursor-movement sequences are
/// interpreted rather than printed literally.
/// </summary>
internal static class ConsoleVt
{
    /// <summary>
    /// Turn on ANSI escape handling for stdout on Windows. Returns true if VT is on (always so on
    /// non-Windows, which honors ANSI natively); false if it couldn't be enabled, so the caller can
    /// skip in-place repainting and fall back to plain output.
    /// </summary>
    public static bool TryEnable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        const int StdOutputHandle = -11;
        const uint EnableVirtualTerminalProcessing = 0x0004;

        var handle = NativeMethods.GetStdHandle(StdOutputHandle);
        return handle != IntPtr.Zero
            && handle != new IntPtr(-1)
            && NativeMethods.GetConsoleMode(handle, out var mode)
            && ((mode & EnableVirtualTerminalProcessing) != 0 || NativeMethods.SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing));
    }

    /// <summary>The kernel32 console P/Invokes used to enable ANSI escape handling on Windows.</summary>
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
    }
}
