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

        // How it renders (colour, the one terminal probe, images and links) is Console.Lib's MarkdownOutput, which
        // writes the raw text when the pipeline throws, so the document always comes out.
        await MarkdownOutput.RenderAsync(content, SysConsole.Out, new MarkdownOutputOptions(
            Mode: options.Mode,
            Unicode: options.Unicode,
            Width: options.Width,
            Color: options.Color,
            BaseDir: MarkdownOutput.DocumentBaseDir(options.FilePath),
            MathFontPath: ResolveMathFont()), SysConsole.Error);
        return 0;
    }

    private static string? ResolveMathFont()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Fonts", "STIX2Math.otf");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <param name="Mode">The raster encoding --mode pinned, or null to auto-detect one.</param>
    /// <param name="Unicode">
    /// --mode unicode: raster nothing. Its own flag because "no encoding" is not "detect one"; when
    /// unicode parsed to a null <see cref="Mode"/> as well, the probe then picked a raster anyway.
    /// </param>
    private record Options(string? FilePath, BoxRenderMode? Mode, bool Unicode, int? Width, ColorMode? Color,
        bool Help = false);

    private static Options? ParseArgs(string[] args)
    {
        string? filePath = null;
        BoxRenderMode? mode = null;
        var unicode = false;
        int? width = null;
        // null = auto: MarkdownOutput.ResolveColorAndTheme detects truecolor and honours
        // NO_COLOR. An explicit --color / --no-color sets a non-null value
        // that overrides detection.
        ColorMode? color = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    return new Options(null, null, false, null, color, Help: true);
                case "--mode" when i + 1 < args.Length:
                    if (!TryParseMode(args[++i], out mode, out unicode))
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

        return new Options(filePath, mode, unicode, width, color);
    }

    private static bool TryParseMode(string s, out BoxRenderMode? mode, out bool unicode)
    {
        unicode = false;
        mode = null;
        switch (s.ToLowerInvariant())
        {
            case "unicode":   unicode = true; return true;
            case "sixel":     mode = BoxRenderMode.Sixel; return true;
            case "sextant":   mode = BoxRenderMode.Sextant; return true;
            case "halfblock": mode = BoxRenderMode.HalfBlock; return true;
            default:          return false;
        }
    }

    private static ColorMode? ParseColor(string s) => s.ToLowerInvariant() switch
    {
        "truecolor" or "true" or "24bit" => ColorMode.TrueColor,
        "16" or "sgr16" or "ansi"        => ColorMode.Sgr16,
        "none" or "off" or "plain"       => ColorMode.None,
        _ => null,
    };

    private static void PrintUsage()
    {
        SysConsole.WriteLine("""
            Usage: mdcat [options] [file]

            Arguments:
              file                  The markdown file to render. Use '-' for stdin.

            Options:
              -h, --help            Show this help message.
              --mode <encoding>     unicode | sixel | sextant | halfblock, for
                                    display math and images. unicode rasters
                                    nothing. Default: auto-detect (sixel on
                                    capable terminals, sextant otherwise).
                                    Without colour nothing rasters.
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
