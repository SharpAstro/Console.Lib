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

        BoxRenderMode? mathMode = options.Mode;
        if (mathMode == null)
        {
            mathMode = await DetectMathModeAsync();
        }

        int width = options.Width ?? GetConsoleWidth();

        var (colorMode, theme) = ResolveColorAndTheme(options.Color);

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
                mathFontPath: ResolveMathFont());
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

    private static async Task<BoxRenderMode?> DetectMathModeAsync()
    {
        try
        {
            await using var probe = new VirtualTerminal();
            await probe.InitAsync();
            return probe.HasSixelSupport
                ? BoxRenderMode.Sixel
                : BoxRenderMode.Sextant;
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

    private static int GetConsoleWidth()
    {
        try
        {
            var w = SysConsole.WindowWidth;
            return w > 0 ? w : 80;
        }
        catch
        {
            return 80;
        }
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
