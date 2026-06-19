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

        MarkdownRenderer.Render(
            content,
            SysConsole.Out,
            width,
            colorMode: ColorMode.TrueColor,
            theme: null,
            mathMode: mathMode,
            mathFontPath: ResolveMathFont());

        return 0;
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

    private record Options(string? FilePath, BoxRenderMode? Mode, int? Width);

    private static Options? ParseArgs(string[] args)
    {
        string? filePath = null;
        BoxRenderMode? mode = null;
        int? width = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    PrintUsage();
                    return null;
                case "--mode" when i + 1 < args.Length:
                    mode = ParseMode(args[++i]);
                    if (mode == (BoxRenderMode)(-1))
                    {
                        SysConsole.Error.WriteLine($"Unknown --mode '{args[i]}'. Expected: unicode | sixel | sextant | halfblock.");
                        return null;
                    }
                    break;
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

        return new Options(filePath, mode, width);
    }

    private static BoxRenderMode? ParseMode(string s) => s.ToLowerInvariant() switch
    {
        "unicode"   => null,
        "sixel"     => BoxRenderMode.Sixel,
        "sextant"   => BoxRenderMode.Sextant,
        "halfblock" => BoxRenderMode.HalfBlock,
        _ => (BoxRenderMode?)(-1),
    };

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
              --width <N>           Render width. Default: console width or 80.

            Examples:
              mdcat README.md
              cat README.md | mdcat -
              mdcat --mode unicode README.md
            """);
    }
}
