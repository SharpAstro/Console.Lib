using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CL = global::Console.Lib;
using SysConsole = System.Console;

namespace Examples.MarkdownConsole;

/// <summary>
/// Iteration harness for <see cref="CL.MarkdownRenderer"/>. Feeds markdown
/// from one of several sources (positional args, <c>--file</c>, stdin, or
/// a baked-in <c>--sample</c>) through the renderer and writes the VT-styled
/// output to stdout. Math rendering mode auto-detects via DA1 query at
/// startup; <c>--mode</c> forces a specific encoding.
///
/// <para><b>Why this exists:</b> the markdown pipeline has enough moving
/// parts (regex preprocessing, Markdig parse, custom inline parsers, the
/// LaTeX sub-pipeline, sixel/sextant rasterisation) that iterating against
/// a real LLM is painful — each test costs a model inference. This tool
/// lets you replay the exact strings a model produced (or curated samples
/// of the patterns that broke before) and see what the renderer does
/// with them, in milliseconds.</para>
///
/// <para>The bundled samples capture regression-fix scenarios: the
/// <c>\boxed{…}</c> final-answer convention, the Lorentz factor expansion,
/// a fenced code block containing math markers that <em>shouldn't</em> be
/// rewritten, and a mixed-content kitchen-sink.</para>
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        SysConsole.OutputEncoding = Encoding.UTF8;

        var parsed = ParseArgs(args);
        if (parsed is null) return 2;

        // Math-mode resolution. Explicit override beats DA1 probe; probe
        // failure falls back to single-row Unicode rendering (no pixel math).
        CL.BoxRenderMode? mathMode = parsed.Mode is { } explicitMode
            ? explicitMode
            : await DetectMathModeAsync();

        var mathFontPath = ResolveMathFont();
        int width = parsed.Width ?? GetConsoleWidth();

        // Resolve which sources to render. In order of priority:
        //   --sample <id>    → render that one sample
        //   --file <path>    → render the file
        //   positional text  → render that text
        //   --stdin          → drain stdin until EOF
        //   (nothing given)  → demo mode: render every built-in sample,
        //                      each labelled with its id, so a bare
        //                      `dotnet run` shows what the renderer can do
        //                      without the user having to know the sample
        //                      names up front.
        IEnumerable<(string Label, string Source)> sources;
        if (!string.IsNullOrEmpty(parsed.Sample))
        {
            if (!Samples.TryGetValue(parsed.Sample, out var s))
            {
                SysConsole.Error.WriteLine($"Unknown sample '{parsed.Sample}'. Available: {string.Join(", ", Samples.Keys)}");
                return 2;
            }
            sources = [(parsed.Sample, s)];
        }
        else if (!string.IsNullOrEmpty(parsed.FilePath))
        {
            if (!File.Exists(parsed.FilePath))
            {
                SysConsole.Error.WriteLine($"File not found: {parsed.FilePath}");
                return 2;
            }
            sources = [(parsed.FilePath, await File.ReadAllTextAsync(parsed.FilePath))];
        }
        else if (!string.IsNullOrEmpty(parsed.PositionalText))
        {
            sources = [("(cli)", parsed.PositionalText)];
        }
        else if (parsed.ReadStdin)
        {
            sources = [("(stdin)", await SysConsole.In.ReadToEndAsync())];
        }
        else
        {
            // Demo / iteration mode.
            sources = Samples.Select(kv => (kv.Key, kv.Value));
        }

        bool first = true;
        foreach (var (label, src) in sources)
        {
            if (!first) SysConsole.WriteLine();
            SysConsole.WriteLine($"── \x1b[1msample: {label}\x1b[0m ─────");
            SysConsole.WriteLine();

            var lines = CL.MarkdownRenderer.RenderLines(
                src, width,
                colorMode: CL.ColorMode.TrueColor,
                theme: null,
                mathMode: mathMode,
                mathFontPath: mathFontPath);

            foreach (var line in lines)
                SysConsole.WriteLine(line);

            first = false;
        }

        return 0;
    }

    /// <summary>
    /// DA1 probe identical to what the chat REPL in testwinai (and any other
    /// consumer) uses. Sixel-capable terminals get pixel-rendered math;
    /// modern Unicode terminals get sextant rendering; older or piped output
    /// falls back to a single-row Unicode pass.
    /// </summary>
    private static async Task<CL.BoxRenderMode?> DetectMathModeAsync()
    {
        try
        {
            await using var probe = new CL.VirtualTerminal();
            await probe.InitAsync();
            return probe.HasSixelSupport
                ? CL.BoxRenderMode.Sixel
                : CL.BoxRenderMode.Sextant;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve a bundled STIX2 math font if it ships alongside the host
    /// (testwinai drops one at <c>AppContext.BaseDirectory/Fonts/STIX2Math.otf</c>);
    /// otherwise let <see cref="CL.MarkdownRenderer"/>'s internal
    /// font-search find one (or fall through to Unicode-only).
    /// </summary>
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

    // ── CLI parsing ──────────────────────────────────────────────────

    private record ParsedArgs(
        string? Sample,
        string? FilePath,
        CL.BoxRenderMode? Mode,
        int? Width,
        string? PositionalText,
        bool ReadStdin);

    private static ParsedArgs? ParseArgs(string[] args)
    {
        string? sample = null;
        string? filePath = null;
        CL.BoxRenderMode? mode = null;
        int? width = null;
        bool readStdin = false;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    PrintUsage();
                    return null;
                case "--sample" when i + 1 < args.Length:
                    sample = args[++i]; break;
                case "--file" when i + 1 < args.Length:
                    filePath = args[++i]; break;
                case "--stdin":
                    readStdin = true; break;
                case "--mode" when i + 1 < args.Length:
                    mode = ParseMode(args[++i]);
                    if (mode is null && !string.Equals(args[i], "unicode", StringComparison.OrdinalIgnoreCase))
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
                    positional.Add(a); break;
            }
        }

        return new ParsedArgs(
            sample,
            filePath,
            mode,
            width,
            positional.Count == 0 ? null : string.Join(' ', positional),
            readStdin);
    }

    private static CL.BoxRenderMode? ParseMode(string s) => s.ToLowerInvariant() switch
    {
        "unicode"   => null,
        "sixel"     => CL.BoxRenderMode.Sixel,
        "sextant"   => CL.BoxRenderMode.Sextant,
        "halfblock" => CL.BoxRenderMode.HalfBlock,
        _ => (CL.BoxRenderMode?)(-1),    // sentinel — distinguishes "unknown" from "unicode"
    };

    private static void PrintUsage()
    {
        SysConsole.WriteLine("""
            Usage: markdown-console [options] [text...]

            Sources (priority order; if none given, all built-in samples are rendered):
              --sample <id>         Render one built-in sample by id.
              --file <path>         Read markdown from a file.
              <text>                Positional args joined with spaces.
              --stdin               Drain stdin until EOF (explicit; not implied by lack of args).

            Rendering:
              --mode <encoding>     unicode | sixel | sextant | halfblock.
                                    Default: auto-detect via DA1 (sixel on capable
                                    terminals, sextant otherwise).
              --width <N>           Render width. Default: console width or 80.

            Examples:
              markdown-console                         # render every built-in sample
              markdown-console --sample boxed
              markdown-console --sample fenced --mode unicode
              markdown-console --file note.md
              cat note.md | markdown-console --stdin
              markdown-console "Energy: \boxed{E = mc^2}"
            """);
    }

    // ── Built-in samples ─────────────────────────────────────────────
    //
    // Real-world model-output patterns that surface rendering bugs. Add
    // more entries as new regressions show up — the cost is a few lines
    // of source-embedded string, the payoff is a one-command repro.

    private static readonly Dictionary<string, string> Samples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["boxed"] =
            "### Final Answer\n\n" +
            "The small-velocity expansion for \\( v \\ll c \\) yields the classical results.\n\n" +
            "Energy: \\boxed{E = mc^2 + \\frac{1}{2}mv^2}\n\n" +
            "Momentum: \\boxed{p = mv}\n",

        ["lorentz"] =
            "To find the small-velocity expansion for when \\( v \\ll c \\), we start with the Lorentz factor \\( \\gamma \\):\n\n" +
            "\\[\n" +
            "\\gamma = \\frac{1}{\\sqrt{1 - \\frac{v^2}{c^2}}}\n" +
            "\\]\n\n" +
            "When \\( v \\ll c \\), the term \\( v^2/c^2 \\) is small. We can use a Taylor series expansion for \\( (1 - x)^{-1/2} \\) where \\( x = v^2/c^2 \\). The expansion is:\n\n" +
            "\\[\n" +
            "(1 - x)^{-1/2} \\approx 1 + \\frac{x}{2} + \\frac{3x^2}{8} + \\cdots\n" +
            "\\]\n\n" +
            "Substituting \\( x = v^2/c^2 \\):\n\n" +
            "\\[\n" +
            "\\gamma \\approx 1 + \\frac{1}{2}\\left(\\frac{v^2}{c^2}\\right) + \\cdots\n" +
            "\\]\n",

        ["mixed"] =
            "# Mixed-content sample\n\n" +
            "**Bold** and *italic* and `inline code`. A link: [example](https://example.com).\n\n" +
            "Inline math: \\( e^{i\\pi} + 1 = 0 \\). Display math:\n\n" +
            "$$ \\int_0^\\infty e^{-x^2}\\,dx = \\frac{\\sqrt{\\pi}}{2} $$\n\n" +
            "- Unordered item one\n" +
            "- Unordered item two with *emphasis*\n\n" +
            "1. Ordered first\n" +
            "2. Ordered second\n\n" +
            "| Col A | Col B |\n" +
            "|:------|------:|\n" +
            "| left  | right |\n",

        ["fenced"] =
            "Here's a fenced code block — the math markers inside should NOT be rendered:\n\n" +
            "```latex\n" +
            "\\[ E = mc^2 \\]\n" +
            "\\frac{1}{2}\\,m v^2\n" +
            "131 \\div 2 \\approx 65.5\n" +
            "```\n\n" +
            "After the fence, normal math: \\( e^{i\\pi} = -1 \\).\n",
    };
}
