namespace Console.Lib;

/// <summary>A process's standard output streams, as a command-line tool writes them.</summary>
public enum StandardStream
{
    /// <summary>Standard output.</summary>
    Output,

    /// <summary>Standard error.</summary>
    Error,
}

/// <summary>
/// Colour on a PLAIN stream: a command-line tool's stdout or stderr written as text, line by line, rather than a
/// screen a <see cref="VirtualTerminal"/> owns. Answers whether the stream may take a colour escape and paints a run of
/// text to match, so a tool that prints its errors in red needs nothing beyond this library to do it.
/// </summary>
/// <remarks>
/// <para>A stream takes colour when three things hold. It is a terminal, not redirected to a file or a pipe, where an
/// escape is noise in whatever reads it. <c>NO_COLOR</c> does not ask for none (<see cref="NoColorRequested"/>). And on
/// Windows the console accepts virtual-terminal processing on that stream's own handle, which is switched on the first
/// time the stream is asked about: a console started without it prints the escape's bytes instead of a colour. Each
/// stream is resolved once per process.</para>
/// <para>The answer is <see cref="ColorMode.Sgr16"/>, the sixteen colours every terminal draws, or
/// <see cref="ColorMode.None"/>. A caller that wants truecolor decides that for itself.</para>
/// </remarks>
public static class StreamColor
{
    private static readonly Lazy<ColorMode> s_output = new(() => Resolve(StandardStream.Output));
    private static readonly Lazy<ColorMode> s_error = new(() => Resolve(StandardStream.Error));

    /// <summary>
    /// Whether the user asked for no colour: <c>NO_COLOR</c> is set to anything but the empty string, as
    /// https://no-color.org states the convention. The one reading of that variable in this library.
    /// </summary>
    public static bool NoColorRequested { get; } = IsNoColor(Environment.GetEnvironmentVariable("NO_COLOR"));

    /// <summary>The colour mode <paramref name="stream"/> takes: <see cref="ColorMode.Sgr16"/>, or <see cref="ColorMode.None"/>.</summary>
    public static ColorMode For(StandardStream stream) => stream is StandardStream.Error ? s_error.Value : s_output.Value;

    /// <summary>
    /// <paramref name="text"/> in the foreground <paramref name="color"/> and then a reset, or the text alone under
    /// <see cref="ColorMode.None"/>.
    /// </summary>
    public static string Paint(string text, SgrColor color, ColorMode mode) => mode is ColorMode.None
        ? text
        : $"{new VtStyle(color.ToRgba(), default).ApplyFg(mode)}{text}{VtStyle.Reset}";

    internal static bool IsNoColor(string? value) => !string.IsNullOrEmpty(value);

    /// <summary>
    /// The rule, apart from the process it reads. <paramref name="takesEscapes"/> is asked last, so a redirected stream
    /// or <c>NO_COLOR</c> never touches the console's mode.
    /// </summary>
    internal static ColorMode Resolve(bool isRedirected, bool noColor, Func<bool> takesEscapes)
        => isRedirected || noColor || !takesEscapes() ? ColorMode.None : ColorMode.Sgr16;

    private static ColorMode Resolve(StandardStream stream) => Resolve(
        stream is StandardStream.Error ? System.Console.IsErrorRedirected : System.Console.IsOutputRedirected,
        NoColorRequested,
        () => !OperatingSystem.IsWindows() || WindowsConsoleInput.EnableVirtualTerminalOutput(stream));
}
