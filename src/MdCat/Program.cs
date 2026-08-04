using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Console.Lib;
using SysConsole = System.Console;

namespace MdCat;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        SysConsole.OutputEncoding = Encoding.UTF8;

        var options = ParseArgs(args);
        if (options is null) return 2;
        if (options.Help) { PrintUsage(); return 0; }

        string content;
        try
        {
            if (options.FilePath == "-" || (options.FilePath is null && SysConsole.IsInputRedirected))
            {
                content = await SysConsole.In.ReadToEndAsync();
            }
            else if (options.FilePath is not null)
            {
                if (!File.Exists(options.FilePath))
                {
                    SysConsole.Error.WriteLine($"File not found: {options.FilePath}");
                    return 1;
                }
                content = await File.ReadAllTextAsync(options.FilePath);
            }
            else
            {
                PrintUsage();
                return 0;
            }
        }
        catch (Exception ex)
        {
            SysConsole.Error.WriteLine($"Error reading input: {ex.Message}");
            return 1;
        }

        int width = options.Width ?? ConsoleSize.GetWidth();

        var (colorMode, theme) = ResolveColorAndTheme(options.Color);

        // A single terminal probe drives both the math-raster mode and image
        // rendering (cell pixel size + Sixel/blocks capability). Skip it only
        // when math mode is already pinned and colour (hence images) is off.
        BoxRenderMode? mathMode = options.Mode;
        MarkdownImageOptions? images = null;
        if (mathMode == null || colorMode != ColorMode.None)
        {
            var probe = await ProbeTerminalAsync();
            if (mathMode == null && probe.Available)
                mathMode = probe.Sixel ? BoxRenderMode.Sixel : BoxRenderMode.Sextant;
            if (colorMode != ColorMode.None)
                images = BuildImageOptions(probe, options.Mode, options.FilePath);
        }

        // Render into a buffer first, then flush. The renderer writes
        // incrementally, so catching around a direct-to-stdout render would
        // still leave half-rendered output before the throw. Buffering keeps
        // mdcat cat-tolerant: if the markdown/LaTeX pipeline throws on some
        // grammar edge case, we emit the raw file contents instead of crashing
        // with a stack trace — you always get the document either way.
        var buffer = new StringWriter();
        try
        {
            MarkdownRenderer.Render(
                content,
                buffer,
                width,
                colorMode: colorMode,
                theme: theme,
                mathMode: mathMode,
                mathFontPath: ResolveMathFont(),
                images: images);
        }
        catch (Exception ex)
        {
            SysConsole.Error.WriteLine($"mdcat: could not render markdown ({ex.Message}); emitting raw text.");
            SysConsole.Out.Write(content);
            return 0;
        }

        SysConsole.Out.Write(buffer.ToString());
        return 0;
    }

    /// <summary>
    /// Resolves the effective <see cref="ColorMode"/> and theme. An explicit
    /// <c>--color</c> / <c>--no-color</c> wins; otherwise NO_COLOR forces plain,
    /// and we keep emitting truecolor escapes (unchanged) but only upgrade to
    /// the richer <see cref="MarkdownTheme.Modern"/> palette when the terminal
    /// is confirmed truecolor — a 16-colour terminal would mangle the hex tones.
    /// </summary>
    private static (ColorMode mode, MarkdownTheme? theme) ResolveColorAndTheme(ColorMode? explicitMode)
    {
        if (explicitMode is { } m)
            return (m, m == ColorMode.TrueColor ? MarkdownTheme.Modern : null);

        if (HasNoColorEnv())
            return (ColorMode.None, null);

        return (ColorMode.TrueColor, SupportsTrueColor() ? MarkdownTheme.Modern : null);
    }

    /// <summary>
    /// Best-effort 24-bit-colour detection. There is no universally reliable
    /// probe, so we use the de-facto conventions: COLORTERM=truecolor|24bit,
    /// then known-terminal env markers (Windows Terminal sets WT_SESSION;
    /// iTerm2 / WezTerm / VS Code set TERM_PROGRAM).
    /// </summary>
    private static bool SupportsTrueColor()
    {
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        if (colorTerm is "truecolor" or "24bit") return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))) return true;
        return Environment.GetEnvironmentVariable("TERM_PROGRAM")
            is "iTerm.app" or "WezTerm" or "vscode";
    }

    private readonly record struct TerminalProbe(
        bool Available, bool Sixel, ImageDisplayCapability ImageCap, int CellW, int CellH);

    private static async Task<TerminalProbe> ProbeTerminalAsync()
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
            return default; // Available == false
        }
    }

    /// <summary>
    /// Builds image-rendering options from the terminal probe. Returns null
    /// (→ alt text) when the terminal can't display images. An explicit
    /// <c>--mode</c> raster choice overrides the detected capability so images
    /// share the math encoding the user asked for.
    /// </summary>
    private static MarkdownImageOptions? BuildImageOptions(TerminalProbe probe, BoxRenderMode? explicitMode, string? filePath)
    {
        if (!probe.Available) return null;
        var mode = explicitMode ?? probe.ImageCap switch
        {
            ImageDisplayCapability.Sixel => BoxRenderMode.Sixel,
            ImageDisplayCapability.AsciiBlock => BoxRenderMode.Sextant,
            _ => (BoxRenderMode?)null, // NoColor → no raster
        };
        if (mode is not { } m) return null;

        var baseDir = ResolveImageBaseDir(filePath);
        var cellW = probe.CellW > 0 ? probe.CellW : 10;
        var cellH = probe.CellH > 0 ? probe.CellH : 20;
        return new MarkdownImageOptions(src => LoadLocalImage(src, baseDir), m, cellW, cellH);
    }

    /// <summary>Directory image sources resolve against: the document's folder, else CWD (stdin).</summary>
    private static string ResolveImageBaseDir(string? filePath)
    {
        if (filePath is not null && filePath != "-")
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            catch { /* fall through to CWD */ }
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Loads a local image file for the renderer. mdcat does NOT fetch over the
    /// network — remote (<c>http(s)://</c>) and <c>data:</c> URLs return null so
    /// the image falls back to its alt text. Relative paths resolve against
    /// <paramref name="baseDir"/>.
    /// </summary>
    private static byte[]? LoadLocalImage(string src, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;
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

    private static string? ResolveMathFont()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Fonts", "STIX2Math.otf");
        return File.Exists(candidate) ? candidate : null;
    }

    private record Options(string? FilePath, BoxRenderMode? Mode, int? Width, ColorMode? Color, bool Help = false);

    private static Options? ParseArgs(string[] args)
    {
        string? filePath = null;
        BoxRenderMode? mode = null;
        int? width = null;
        // null = auto: ResolveColorAndTheme detects truecolor and honours
        // NO_COLOR. An explicit --color / --no-color sets a non-null value
        // that overrides detection.
        ColorMode? color = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    return new Options(null, null, null, color, Help: true);
                case "--mode" when i + 1 < args.Length:
                    mode = ParseMode(args[++i]);
                    if (mode == (BoxRenderMode)(-1))
                    {
                        SysConsole.Error.WriteLine($"Unknown --mode '{args[i]}'. Expected: unicode | sixel | sextant | halfblock.");
                        return null;
                    }
                    break;
                case "--color" when i + 1 < args.Length:
                    var c = ParseColor(args[++i]);
                    if (c is null)
                    {
                        SysConsole.Error.WriteLine($"Unknown --color '{args[i]}'. Expected: truecolor | 16 | none.");
                        return null;
                    }
                    color = c.Value; break;
                case "--no-color" or "--plain":
                    color = ColorMode.None; break;
                case "--width" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var w) || w <= 0)
                    {
                        SysConsole.Error.WriteLine($"Invalid --width '{args[i]}'.");
                        return null;
                    }
                    width = w; break;
                default:
                    if (a.StartsWith("-") && a != "-")
                    {
                        SysConsole.Error.WriteLine($"Unknown option '{a}'");
                        return null;
                    }
                    if (filePath != null)
                    {
                        SysConsole.Error.WriteLine("Multiple files specified.");
                        return null;
                    }
                    filePath = a;
                    break;
            }
        }

        return new Options(filePath, mode, width, color);
    }

    private static BoxRenderMode? ParseMode(string s) => s.ToLowerInvariant() switch
    {
        "unicode"   => null,
        "sixel"     => BoxRenderMode.Sixel,
        "sextant"   => BoxRenderMode.Sextant,
        "halfblock" => BoxRenderMode.HalfBlock,
        _ => (BoxRenderMode?)(-1),
    };

    private static ColorMode? ParseColor(string s) => s.ToLowerInvariant() switch
    {
        "truecolor" or "true" or "24bit" => ColorMode.TrueColor,
        "16" or "sgr16" or "ansi"        => ColorMode.Sgr16,
        "none" or "off" or "plain"       => ColorMode.None,
        _ => null,
    };

    private static bool HasNoColorEnv()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    private static void PrintUsage()
    {
        SysConsole.WriteLine("""
            Usage: mdcat [options] [file]

            Arguments:
              file                  The markdown file to render. Use '-' for stdin.

            Options:
              -h, --help            Show this help message.
              --mode <encoding>     unicode | sixel | sextant | halfblock.
                                    Default: auto-detect (sixel on capable
                                    terminals, sextant otherwise).
              --color <mode>        truecolor | 16 | none. Default: auto-detect
                                    (truecolor + modern palette on capable
                                    terminals; honours NO_COLOR).
              --no-color, --plain   Plain text, no escape sequences.
              --width <N>           Render width. Default: console width or 80.

            Examples:
              mdcat README.md
              cat README.md | mdcat -
              mdcat --mode unicode README.md
              mdcat --plain README.md > README.txt
            """);
    }
}
