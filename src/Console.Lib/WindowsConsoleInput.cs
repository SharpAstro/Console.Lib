using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Console.Lib;

[SupportedOSPlatform("windows")]
internal static class WindowsConsoleInput
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_INPUT_HANDLE = -10;

    /// <summary>
    /// Provides native Windows console input handling, including mouse events.
    /// </summary>
    [Flags]
    private enum ConsoleMode : uint
    {
        None = 0,
        Processed = 0x0001,
        VirtualTerminalProcessing = 0x0004,
        WindowInput = 0x0008,
        MouseInput = 0x0010,
        QuickEditMode = 0x0040,
        ExtendedFlags = 0x0080,
        VirtualTerminalInput = 0x0200,
    }


    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out ConsoleMode lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, ConsoleMode dwMode);

    private static nint _inputHandle;
    private static nint _outputHandle;
    private static ConsoleMode _originalInputMode;
    private static ConsoleMode _originalOutputMode;
    private static bool _enabled;

    /// <summary>
    /// Enables virtual terminal input and output processing.
    /// Only takes effect on the first call; subsequent calls are no-ops.
    /// </summary>
    /// <returns>True if virtual terminal input and output processing was enabled successfully.</returns>
    public static bool EnableVirtualTerminalIO()
    {
        if (_enabled)
        {
            return true;
        }

        _inputHandle = GetStdHandle(STD_INPUT_HANDLE);
        if (_inputHandle == nint.Zero || _inputHandle == new nint(-1))
        {
            return false;
        }

        if (!GetConsoleMode(_inputHandle, out _originalInputMode))
        {
            return false;
        }

        _outputHandle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (!GetConsoleMode(_outputHandle, out _originalOutputMode))
        {
            return false;
        }

        var newInputMode = (
            ConsoleMode.VirtualTerminalInput
            | ConsoleMode.Processed
            | ConsoleMode.WindowInput
            | ConsoleMode.MouseInput
            | ConsoleMode.ExtendedFlags
        ) & ~ConsoleMode.QuickEditMode;

        _enabled = SetConsoleMode(_inputHandle, newInputMode)
            && SetConsoleMode(_outputHandle, _originalOutputMode | ConsoleMode.Processed | ConsoleMode.VirtualTerminalProcessing);
        return _enabled;
    }

    /// <summary>
    /// Restores the original console mode.
    /// </summary>
    public static void RestoreConsoleMode()
    {
        if (_inputHandle != nint.Zero && _inputHandle != new nint(-1))
        {
            SetConsoleMode(_inputHandle, _originalInputMode);
        }

        if (_outputHandle != nint.Zero && _outputHandle != new nint(-1))
        {
            SetConsoleMode(_outputHandle, _originalOutputMode);
        }
    }

    // ── Console size, read from CONOUT$ ─────────────────────────────────────
    //
    // System.Console.WindowWidth/Height size via GetStdHandle(STD_OUTPUT_HANDLE),
    // which IS the pipe once stdout is redirected — so they throw exactly when a
    // caller most wants an answer (mdcat piped to a pager, a host capturing our
    // output). CONOUT$ opened directly names the ATTACHED CONSOLE's active screen
    // buffer, independent of whatever stdout happens to be wired to, so the real
    // window is still reachable. Nothing here writes to that handle; it is opened
    // for GetConsoleScreenBufferInfo alone.

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x0001;
    private const uint FILE_SHARE_WRITE = 0x0002;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        /// <summary>The SCROLLBACK buffer's extent — its height is the scroll history, not the
        /// visible window. Never read below; the window comes from <see cref="srWindow"/>.</summary>
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        /// <summary>The visible window rect, in buffer coordinates. Inclusive on all four edges.</summary>
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    // GENERIC_WRITE is requested alongside GENERIC_READ because a console opened
    // read-only can refuse the screen-buffer query on some Windows hosts.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(nint hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    private static nint _conoutHandle;
    private static bool _conoutResolved;

    /// <summary>
    /// Opens CONOUT$ once per process and caches the result — including a failure,
    /// which means there is no console attached at all (a detached service, CI).
    /// Nothing in this library calls AttachConsole/FreeConsole, so that verdict
    /// cannot go stale, and <see cref="VirtualTerminal.Size"/> is read on every
    /// idle poll — too hot to reopen a handle for. The handle is deliberately
    /// never closed: at most one exists and the OS reclaims it at exit, the same
    /// as the std handles above.
    /// </summary>
    private static nint GetConoutHandle()
    {
        if (_conoutResolved)
        {
            return _conoutHandle;
        }

        _conoutHandle = CreateFileW(
            "CONOUT$",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero,
            OPEN_EXISTING,
            0,
            nint.Zero);
        _conoutResolved = true;
        return _conoutHandle;
    }

    /// <summary>
    /// Reads the attached console's current window size straight from its screen
    /// buffer, bypassing the redirected stdout that makes
    /// <see cref="System.Console.WindowWidth"/> throw. Read at call time, so a
    /// live window resize shows up on the next call.
    /// </summary>
    /// <returns>False when there is no console to ask — the caller's own fallback applies.</returns>
    public static bool TryGetConsoleScreenBufferSize(out int width, out int height)
    {
        width = 0;
        height = 0;

        var handle = GetConoutHandle();
        if (handle == nint.Zero || handle == new nint(-1))
        {
            return false;
        }

        return GetConsoleScreenBufferInfo(handle, out var info)
            && ConsoleSize.TryComputeSize(
                info.srWindow.Left, info.srWindow.Top, info.srWindow.Right, info.srWindow.Bottom,
                out width, out height);
    }
}
