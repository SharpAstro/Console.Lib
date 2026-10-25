using System;
using System.IO;
using System.Threading.Tasks;

namespace Console.Lib;

/// <summary>What a CLI writes to its standard output.</summary>
public enum OutputFormat
{
    /// <summary><see cref="Rich"/> on a terminal that shows Sixel and nothing in the way; <see cref="Plain"/> otherwise.</summary>
    Auto,

    /// <summary>Markdown rendered for the terminal: colour, OSC 8 links, tables, and images where it can raster them.</summary>
    Rich,

    /// <summary>The Markdown source itself, for a file or another renderer.</summary>
    Markdown,

    /// <summary>Text with no escape sequences: what a pipe, a script or an agent reads.</summary>
    Plain,
}

/// <summary>What one terminal probe found: whether it answered, Sixel, image capability, and the cell's pixel size.</summary>
public readonly record struct TerminalProbe(bool Available, bool Sixel, ImageDisplayCapability ImageCap, int CellW, int CellH);

/// <summary>How <see cref="MarkdownOutput.RenderAsync"/> renders: every field left at its default detects or takes the usual.</summary>
/// <param name="Mode">The raster encoding pinned for display math and images, or null to pick one from the probe.</param>
/// <param name="Unicode">Raster nothing: math as Unicode, images as alt text. Its own flag, as "no encoding" is not "detect one".</param>
/// <param name="Width">Columns to render to; the console's width (or 80) when null.</param>
/// <param name="Color">An explicit colour mode; null detects truecolor and honours <c>NO_COLOR</c>.</param>
/// <param name="BaseDir">Folder relative images and links resolve against; the current one when null.</param>
/// <param name="MathFontPath">The math font; DIR.Lib's default when null.</param>
/// <param name="Probe">A probe already taken (by <see cref="MarkdownOutput.ResolveFormatAsync"/>), so the terminal is not asked twice.</param>
public sealed record MarkdownOutputOptions(
    BoxRenderMode? Mode = null,
    bool Unicode = false,
    int? Width = null,
    ColorMode? Color = null,
    string? BaseDir = null,
    string? MathFontPath = null,
    TerminalProbe? Probe = null);

/// <summary>
/// Markdown to a CLI's standard output, as mdcat writes it: colour and theme, one terminal probe for math and images,
/// local images and <c>file://</c> links, and the raw text when rendering fails. Lifted out of mdcat so every SharpAstro
/// CLI that prints Markdown (pilot's status, tianwen's plans) decides the same way.
/// </summary>
public static class MarkdownOutput
{
    /// <summary>Parses an <c>--output-format</c> value: auto, rich, markdown (md) or plain (text), any case.</summary>
    public static bool TryParseFormat(string? value, out OutputFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "auto": format = OutputFormat.Auto; return true;
            case "rich": format = OutputFormat.Rich; return true;
            case "markdown" or "md": format = OutputFormat.Markdown; return true;
            case "plain" or "text": format = OutputFormat.Plain; return true;
            default: format = OutputFormat.Auto; return false;
        }
    }

    /// <summary>
    /// The format to write in, and the probe taken to decide it (to pass on in <see cref="MarkdownOutputOptions.Probe"/>).
    /// <see cref="OutputFormat.Auto"/> is <see cref="OutputFormat.Rich"/> only when standard output is a terminal, no
    /// <c>NO_COLOR</c> is set, and the terminal says it shows Sixel; anything else is <see cref="OutputFormat.Plain"/>.
    /// The redirect is checked before the terminal is asked anything, so a pipe never waits on a probe.
    /// </summary>
    public static async Task<(OutputFormat Format, TerminalProbe? Probe)> ResolveFormatAsync(OutputFormat requested)
    {
        if (requested is not OutputFormat.Auto)
        {
            return (requested, null);
        }
        if (System.Console.IsOutputRedirected || StreamColor.NoColorRequested)
        {
            return (OutputFormat.Plain, null);
        }
        var probe = await ProbeAsync();
        return (probe.Available && probe.Sixel ? OutputFormat.Rich : OutputFormat.Plain, probe);
    }

    /// <summary>
    /// Renders <paramref name="markdown"/> to <paramref name="output"/>. It is rendered into a buffer first, since the
    /// renderer writes as it goes: when the Markdown or LaTeX pipeline throws on some grammar edge case, the raw text is
    /// written instead and the reason goes to <paramref name="error"/>, so the document always comes out. False then.
    /// </summary>
    public static async Task<bool> RenderAsync(string markdown, TextWriter output, MarkdownOutputOptions? options = null, TextWriter? error = null)
    {
        options ??= new MarkdownOutputOptions();
        var width = options.Width ?? ConsoleSize.GetWidth();
        var baseDir = options.BaseDir ?? Directory.GetCurrentDirectory();
        var (colorMode, theme) = ResolveColorAndTheme(options.Color);

        // A single terminal probe drives both the math-raster mode and image rendering (cell pixel size + Sixel/blocks
        // capability). It is skipped whenever nothing can raster: under Unicode, and without colour, since the renderer
        // keeps ColorMode.None free of escape sequences and so takes the Unicode path and alt text there anyway.
        var mathMode = options.Mode;
        MarkdownImageOptions? images = null;
        if (!options.Unicode && colorMode != ColorMode.None)
        {
            var probe = options.Probe ?? await ProbeAsync();
            if (mathMode == null && probe.Available)
            {
                mathMode = probe.Sixel ? BoxRenderMode.Sixel : BoxRenderMode.Sextant;
            }
            images = BuildImageOptions(probe, options.Mode, baseDir);
        }

        var buffer = new StringWriter();
        try
        {
            MarkdownRenderer.Render(markdown, buffer, width, colorMode: colorMode, theme: theme, mathMode: mathMode,
                mathFontPath: options.MathFontPath, images: images, linkResolver: url => ResolveLocalLink(url, baseDir));
        }
        catch (Exception ex)
        {
            error?.WriteLine($"could not render markdown ({ex.Message}); emitting raw text.");
            output.Write(markdown);
            return false;
        }
        output.Write(buffer.ToString());
        return true;
    }

    /// <summary>
    /// The effective <see cref="ColorMode"/> and theme. An explicit mode wins; otherwise NO_COLOR forces plain, and
    /// truecolor escapes are kept but only upgraded to the richer <see cref="MarkdownTheme.Modern"/> palette when the
    /// terminal is confirmed truecolor, since a 16-colour terminal would mangle the hex tones.
    /// </summary>
    public static (ColorMode Mode, MarkdownTheme? Theme) ResolveColorAndTheme(ColorMode? explicitMode)
    {
        if (explicitMode is { } m)
        {
            return (m, m == ColorMode.TrueColor ? MarkdownTheme.Modern : null);
        }
        if (StreamColor.NoColorRequested)
        {
            return (ColorMode.None, null);
        }
        return (ColorMode.TrueColor, SupportsTrueColor() ? MarkdownTheme.Modern : null);
    }

    /// <summary>
    /// Best-effort 24-bit-colour detection. There is no universally reliable probe, so this uses the de-facto
    /// conventions: COLORTERM=truecolor|24bit, then known-terminal env markers (Windows Terminal sets WT_SESSION;
    /// iTerm2, WezTerm and VS Code set TERM_PROGRAM).
    /// </summary>
    public static bool SupportsTrueColor()
    {
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        if (colorTerm is "truecolor" or "24bit")
        {
            return true;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
        {
            return true;
        }
        return Environment.GetEnvironmentVariable("TERM_PROGRAM") is "iTerm.app" or "WezTerm" or "vscode";
    }

    /// <summary>Asks the terminal once (DA1 and cell size); <see cref="TerminalProbe.Available"/> is false when it cannot be asked.</summary>
    public static async Task<TerminalProbe> ProbeAsync()
    {
        try
        {
            await using var probe = new VirtualTerminal();
            await probe.InitAsync();
            var cell = probe.CellSize;
            return new TerminalProbe(true, probe.HasSixelSupport, probe.ImageDisplayCapability, cell.Width, cell.Height);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Image-rendering options from the probe, or null (alt text) when the terminal can't display images. An explicit
    /// <paramref name="explicitMode"/> overrides the detected capability, so images share the math encoding asked for.
    /// </summary>
    public static MarkdownImageOptions? BuildImageOptions(TerminalProbe probe, BoxRenderMode? explicitMode, string baseDir)
    {
        if (!probe.Available)
        {
            return null;
        }
        var mode = explicitMode ?? probe.ImageCap switch
        {
            ImageDisplayCapability.Sixel => BoxRenderMode.Sixel,
            ImageDisplayCapability.AsciiBlock => BoxRenderMode.Sextant,
            _ => (BoxRenderMode?)null, // NoColor: no raster
        };
        if (mode is not { } m)
        {
            return null;
        }
        var cellW = probe.CellW > 0 ? probe.CellW : 10;
        var cellH = probe.CellH > 0 ? probe.CellH : 20;
        return new MarkdownImageOptions(src => LoadLocalImage(src, baseDir), m, cellW, cellH);
    }

    /// <summary>The folder relative image sources and links resolve against: the document's, else the current one (stdin).</summary>
    public static string DocumentBaseDir(string? filePath)
    {
        if (filePath is not null && filePath != "-")
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (!string.IsNullOrEmpty(dir))
                {
                    return dir;
                }
            }
            catch
            {
                // the current folder, below
            }
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// A local image file's bytes. Nothing is fetched over the network: remote (<c>http(s)://</c>) and <c>data:</c> URLs
    /// return null, so the image falls back to its alt text. Relative paths resolve against <paramref name="baseDir"/>.
    /// </summary>
    public static byte[]? LoadLocalImage(string src, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(src))
        {
            return null;
        }
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            var path = Path.IsPathRooted(src) ? src : Path.Combine(baseDir, src);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A Markdown link's href as an OSC 8 target. A bare relative href (<c>docs/foo.md</c>, <c>LICENSE</c>) is not an
    /// absolute URI, so terminals reject it as a clickable target ("invalid link" in Windows Terminal) though its label
    /// still renders. An href that already parses as an absolute URI (<c>http(s)://</c>, <c>mailto:</c>, <c>file://</c>,
    /// or, by <see cref="Uri"/>'s own parsing, a rooted Windows path or UNC share) is left as it is, and so is an in-page
    /// <c>#</c> anchor; everything else resolves against <paramref name="baseDir"/> into a <c>file://</c> URI.
    /// </summary>
    public static string ResolveLocalLink(string url, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(url) || url[0] == '#')
        {
            return url;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }
        try
        {
            return new Uri(Path.GetFullPath(Path.Combine(baseDir, url))).AbsoluteUri;
        }
        catch
        {
            return url;
        }
    }
}
