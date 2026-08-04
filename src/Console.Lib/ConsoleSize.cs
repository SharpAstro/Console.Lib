namespace Console.Lib;

/// <summary>
/// Resolves the terminal's size in columns and rows, covering the one case
/// <see cref="System.Console"/> gets wrong.
/// <para>
/// On Windows, <see cref="System.Console.WindowWidth"/> /
/// <see cref="System.Console.WindowHeight"/> size via
/// <c>GetStdHandle(STD_OUTPUT_HANDLE)</c> — which IS the pipe once stdout is
/// redirected, not the console. So they throw <see cref="System.IO.IOException"/>
/// precisely when output is being piped or captured, and a caller that swallows
/// that exception silently lays out to a guessed width for the rest of the
/// process. The console is still attached and still has a real width; only the
/// handle the CLR asked was the wrong one.
/// </para>
/// <para>
/// <see cref="TryGetWindowSize"/> therefore tries the managed properties first
/// (right on every OS, and on Windows whenever stdout is not redirected) and
/// falls back — Windows only — to opening the attached console's screen buffer
/// directly. Both paths read at call time, so a live window resize is picked up
/// by the next call rather than baked in at startup. Non-Windows behaviour is
/// exactly <see cref="System.Console"/>'s.
/// </para>
/// </summary>
public static class ConsoleSize
{
    /// <summary>
    /// Attempts to resolve the terminal's current size.
    /// </summary>
    /// <returns>
    /// False only when there is genuinely no console to measure — no TTY, a
    /// detached service, CI — at which point the caller's own default applies.
    /// </returns>
    public static bool TryGetWindowSize(out int width, out int height)
    {
        try
        {
            width = System.Console.WindowWidth;
            height = System.Console.WindowHeight;
            if (width > 0 && height > 0)
            {
                return true;
            }
        }
        catch (System.IO.IOException)
        {
            // Redirected or invalid console handle — the case CONOUT$ exists to answer.
        }
        catch (PlatformNotSupportedException)
        {
            // No console concept at all (wasm/browser); nothing below will help either.
        }

        width = 0;
        height = 0;

        return OperatingSystem.IsWindows()
            && WindowsConsoleInput.TryGetConsoleScreenBufferSize(out width, out height);
    }

    /// <summary>
    /// The width alone, for callers that lay out to a column count and want one
    /// number: 80 is the conventional last resort when there is no console.
    /// </summary>
    public static int GetWidth(int fallback = 80)
        => TryGetWindowSize(out var width, out _) ? width : fallback;

    /// <summary>
    /// Turns a Windows <c>CONSOLE_SCREEN_BUFFER_INFO.srWindow</c> rect into a size.
    /// Right and Bottom name the last INCLUDED cell, so the extent is
    /// <c>Right - Left + 1</c> — and the rect to use is <c>srWindow</c>, never the
    /// struct's other size field <c>dwSize</c>, whose height is the scrollback
    /// buffer rather than the visible window.
    /// <para>
    /// Kept here rather than beside the P/Invoke because <see cref="WindowsConsoleInput"/>
    /// is <c>[SupportedOSPlatform("windows")]</c>, and this arithmetic is worth
    /// testing on the Linux runner that actually runs the tests.
    /// </para>
    /// </summary>
    internal static bool TryComputeSize(int left, int top, int right, int bottom, out int width, out int height)
    {
        width = right - left + 1;
        height = bottom - top + 1;
        return width > 0 && height > 0;
    }
}
