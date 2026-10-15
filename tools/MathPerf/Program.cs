// Timing harness for display math. From the repo root:
//
//   dotnet run -c Release --project tools/MathPerf [--baseline <ref>] [--iterations <n>]
//
// It times MarkdownRenderer.RenderLines over MathCorpus (the formulas MathRenderParityTests pins) for two
// Console.Libs in one process: the one this project references ("now", the working tree) and the one at a
// baseline commit ("main", by default cac44a8, where leg console-math-perf branched off). The baseline's src/
// is taken with `git archive`, built Release into obj/baseline/<sha>/ against the DIR.Lib package and loaded
// into its own AssemblyLoadContext. Everything else (DIR.Lib, the codecs) is shared, so both sides parse and
// rasterize with the same code and only Console.Lib differs. It also times the Sixel encoder on its own,
// checks that both sides render every case to the same lines, and counts how many times each side's
// width-fit loop rasterized the wide formulas.

using System.Diagnostics;
using System.Formats.Tar;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Console.Lib;
using Console.Lib.Tests;
using DIR.Lib;
using DIR.Lib.Markdown;
using DIR.Lib.MathLayout;
using LALR.CC.LexicalGrammar;
using Out = System.Console;

var baselineRef = "cac44a8";
var iterations = 21;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--baseline" when i + 1 < args.Length: baselineRef = args[++i]; break;
        case "--iterations" when i + 1 < args.Length: iterations = int.Parse(args[++i]); break;
        default:
            Out.Error.WriteLine($"unknown argument '{args[i]}'; usage: [--baseline <ref>] [--iterations <n>]");
            return 2;
    }
}

var font = Path.Combine(AppContext.BaseDirectory, "Fonts", "STIX2Math.otf");
var repo = Run("git", AppContext.BaseDirectory, "rev-parse", "--show-toplevel").Trim();
var baselineSha = Run("git", repo, "rev-parse", "--verify", baselineRef + "^{commit}").Trim();
var head = Run("git", repo, "rev-parse", "--short=7", "HEAD").Trim();
var dirty = Run("git", repo, "status", "--porcelain", "--", "src").Length > 0;

var baselineAssembly = LoadBaseline(repo, baselineSha);
var main = Side.FromBaseline("main", baselineAssembly, font);
var now = Side.FromWorkingTree("now", font);

var optimized = typeof(MarkdownRenderer).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;
Out.WriteLine($"display-math timing: main = {baselineSha[..7]}, now = {head}{(dirty ? " + uncommitted changes in src/" : "")}");
Out.WriteLine($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} cores, " +
    $"Console.Lib {(optimized ? "Release" : "DEBUG (timings meaningless)")}, DIR.Lib {typeof(Box).Assembly.GetName().Version} shared by both");
Out.WriteLine($"font {Path.GetFileName(font)}, ColorMode.TrueColor, cell 10 px; median of {iterations} calls per case, the two sides interleaved");
Out.WriteLine();

// ── Same output ─────────────────────────────────────────────────────────

var cases = (from f in MathCorpus.Formulas
             from mode in MathCorpus.Modes
             from width in MathCorpus.Widths
             select (f.Name, Markdown: MathCorpus.Markdown(f.Source), f.Source, Mode: mode, Width: width)).ToList();
var differing = cases.Where(c => !main.Render(c.Markdown, c.Width, c.Mode).SequenceEqual(now.Render(c.Markdown, c.Width, c.Mode))).ToList();
Out.WriteLine($"RenderLines output identical, main vs now: {cases.Count - differing.Count}/{cases.Count} cases");
foreach (var c in differing) Out.WriteLine($"  DIFFERS: {c.Name} {c.Mode} {c.Width}");
Out.WriteLine();

// ── RenderLines, per formula ────────────────────────────────────────────

var medians = new Dictionary<(string, BoxRenderMode, int), (double Main, double Now)>();
foreach (var c in cases)
    medians[(c.Name, c.Mode, c.Width)] = TimePair(() => main.Render(c.Markdown, c.Width, c.Mode), () => now.Render(c.Markdown, c.Width, c.Mode), iterations);

foreach (var mode in MathCorpus.Modes)
{
    Out.WriteLine($"RenderLines, {mode}: median ms per call");
    Out.Write($"  {"formula",-20}");
    foreach (var width in MathCorpus.Widths) Out.Write($" | {"width " + width,-21}");
    Out.WriteLine();
    Out.Write($"  {"",-20}");
    foreach (var _ in MathCorpus.Widths) Out.Write($" | {"main",6} {"now",6} {"x",6} ");
    Out.WriteLine();
    foreach (var (name, _) in MathCorpus.Formulas)
    {
        Out.Write($"  {name,-20}");
        foreach (var width in MathCorpus.Widths)
        {
            var (m, n) = medians[(name, mode, width)];
            Out.Write($" | {m,6:F2} {n,6:F2} {m / n,5:F1}x ");
        }
        Out.WriteLine();
    }
    Out.Write($"  {"sum of medians",-20}");
    foreach (var width in MathCorpus.Widths)
    {
        var m = MathCorpus.Formulas.Sum(f => medians[(f.Name, mode, width)].Main);
        var n = MathCorpus.Formulas.Sum(f => medians[(f.Name, mode, width)].Now);
        Out.Write($" | {m,6:F1} {n,6:F1} {m / n,5:F1}x ");
    }
    Out.WriteLine();
    Out.WriteLine();
}

// ── Rasterizations in the width-fit loop ───────────────────────────────

var nowCounter = typeof(MarkdownRenderer).GetField("t_mathRasterizations", BindingFlags.NonPublic | BindingFlags.Static);
Out.WriteLine("Rasterizations per RenderLines call, wide formulas: main -> now (outcome)");
Out.WriteLine(nowCounter is null
    ? "  (now has no t_mathRasterizations counter, so only main's count is shown)"
    : "  main's count replays its width-fit loop against DIR.Lib; now's is MarkdownRenderer's own counter");
foreach (var name in MathCorpus.Wide)
{
    var source = MathCorpus.Formulas.Single(f => f.Name == name).Source;
    foreach (var mode in MathCorpus.Modes)
    {
        Out.Write($"  {name + " " + mode,-28}");
        foreach (var width in MathCorpus.Widths)
        {
            var (count, outcome) = MainLoopRasterizations(source, mode, width, font);
            var nowCount = "?";
            if (nowCounter?.GetValue(null) is int before)
            {
                now.Render(MathCorpus.Markdown(source), width, mode);
                nowCount = nowCounter.GetValue(null) is int after ? (after - before).ToString() : "?";
            }
            Out.Write($" | w{width}: {count} -> {nowCount} ({outcome})".PadRight(36));
        }
        Out.WriteLine();
    }
}
var mainTotal = cases.Sum(c => MainLoopRasterizations(c.Source, c.Mode, c.Width, font).Count);
if (nowCounter?.GetValue(null) is int start)
{
    foreach (var c in cases) now.Render(c.Markdown, c.Width, c.Mode);
    var nowTotal = nowCounter.GetValue(null) is int end ? end - start : -1;
    Out.WriteLine($"  whole corpus, all {cases.Count} cases: main {mainTotal} rasterizations, now {nowTotal}");
}
else
{
    Out.WriteLine($"  whole corpus, all {cases.Count} cases: main {mainTotal} rasterizations");
}
Out.WriteLine();

// ── Sixel encoder on its own ────────────────────────────────────────────

Out.WriteLine("Sixel encode on its own (32 px rasters, as display math sends them): median ms");
var encodeIterations = Math.Max(iterations, 101);
var rasters = new List<(string Name, RgbaImage Image)>();
foreach (var (name, source) in MathCorpus.Formulas)
{
    if (Rasterize(source, 32f, font) is { Width: > 0, Height: > 0 } image) rasters.Add((name, image));
}
var sameBytes = rasters.All(r => main.EncodeBytes(r.Image).AsSpan().SequenceEqual(now.EncodeBytes(r.Image)));
Out.WriteLine($"  encoded bytes identical, main vs now: {(sameBytes ? "yes" : "NO")}, {rasters.Count} rasters");
Out.WriteLine($"  {"raster",-30} {"SixelEncoder.Encode",-22} {"BoxRenderer.EncodeImage",-22}");
Out.WriteLine($"  {"",-30} {"main",6} {"now",6} {"x",6}   {"main",6} {"now",6} {"x",6}");
double encodeMain = 0, encodeNow = 0, imageMain = 0, imageNow = 0;
foreach (var (name, img) in rasters)
{
    var encode = TimePair(() => main.EncodeBytes(img), () => now.EncodeBytes(img), encodeIterations);
    var encodeImage = TimePair(() => main.EncodeImage(img), () => now.EncodeImage(img), encodeIterations);
    encodeMain += encode.Main; encodeNow += encode.Now; imageMain += encodeImage.Main; imageNow += encodeImage.Now;
    if (name is "quadratic" or "fourteen-terms" or "fine-structure" or "mass-energy")
        Out.WriteLine($"  {$"{name} {img.Width}x{img.Height}",-30} {encode.Main,6:F3} {encode.Now,6:F3} {encode.Main / encode.Now,5:F1}x   " +
            $"{encodeImage.Main,6:F3} {encodeImage.Now,6:F3} {encodeImage.Main / encodeImage.Now,5:F1}x");
}
Out.WriteLine($"  {$"sum over all {rasters.Count}",-30} {encodeMain,6:F2} {encodeNow,6:F2} {encodeMain / encodeNow,5:F1}x   " +
    $"{imageMain,6:F2} {imageNow,6:F2} {imageMain / imageNow,5:F1}x");
return 0;

// ── Helpers ─────────────────────────────────────────────────────────────

static (double Main, double Now) TimePair(Action main, Action now, int iterations)
{
    main(); now(); main(); now();
    var a = new double[iterations];
    var b = new double[iterations];
    for (var i = 0; i < iterations; i++)
    {
        // Alternate which side goes first, so neither always runs on a cache the other just warmed.
        if (i % 2 == 0) { a[i] = Time(main); b[i] = Time(now); }
        else { b[i] = Time(now); a[i] = Time(main); }
    }
    Array.Sort(a);
    Array.Sort(b);
    return (a[iterations / 2], b[iterations / 2]);

    // A RenderLines call allocates several megabytes (DIR.Lib's markdown parse alone is about 4.8 MB) and
    // its rasters are big enough for the large object heap, so gen2 collections land on whichever sample
    // happens to be running and swing a single case's median by 2x either way. Collecting before each
    // sample, outside the timing, bills every call for its own garbage only.
    static double Time(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var t0 = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
}

// main's width-fit loop, replayed with the DIR.Lib calls it made (cac44a8, MarkdownRenderer.TryRenderMathBox):
// every pass parses, builds the box tree and rasterizes, then measures the raster. Returns how many
// rasterizations that took and what the call ended with.
static (int Count, string Outcome) MainLoopRasterizations(string source, BoxRenderMode mode, int width, string font)
{
    const int cellPixelWidth = 10;
    if (MarkdownMacros.ContainsMacro(source, "text") || source.Contains(@"\begin", StringComparison.Ordinal))
        return (0, "unicode");
    source = MarkdownMacros.NormalizeLatexAliases(source);
    source = MarkdownMacros.ExpandBalancedMacro(source, "boxed", inner => inner);
    source = MarkdownMacros.ExpandBalancedMacro(source, "ce", inner => Mhchem.ToLatex(inner));
    if (MarkdownMacros.ContainsMacro(source, "text") || source.Contains(@"\begin", StringComparison.Ordinal))
        return (0, "unicode");
    source = MarkdownMacros.ResolveBackslashEscapes(source);

    var defaultSize = mode switch { BoxRenderMode.Sixel => 32f, BoxRenderMode.Sextant => 12f, _ => 10f };
    var minimumSize = defaultSize * 2 / 3;
    var fontSize = defaultSize;
    var count = 0;
    try
    {
        while (true)
        {
            var style = new BoxStyle(font, fontSize);
            var parser = Latex.BuildParser(new BoxBuildingVisitor(style));
            using var lexer = BytesLexer.FromString(source, MarkdownMacros.MathLexerTable);
            using var tokens = new SyncLATokenIterator(lexer);
            var item = parser.ParseInput(tokens, debugger: null);
            if (item.IsError || item.Content is not Func<BoxStyle, Box> builder) return (count, "unicode");
            var image = BoxRasterizer.RenderToRgba(builder(style), style);
            count++;
            if (image.Width <= 0 || image.Height <= 0) return (count, "unicode");
            var cells = mode switch
            {
                BoxRenderMode.Sixel => (image.Width + cellPixelWidth - 1) / cellPixelWidth,
                BoxRenderMode.Sextant => (image.Width + 1) / 2,
                _ => image.Width,
            };
            if (cells > width)
            {
                var next = Math.Min(fontSize * width / cells, fontSize - 0.5f);
                if (next < minimumSize) return (count, "too wide, unicode");
                fontSize = next;
                continue;
            }
            return (count, fontSize == defaultSize ? "fits" : $"shrunk to {fontSize:0.##} px");
        }
    }
    catch
    {
        return (count, "unicode");
    }
}

static RgbaImage? Rasterize(string source, float fontSize, string font)
{
    try
    {
        var style = new BoxStyle(font, fontSize);
        var parser = Latex.BuildParser(new BoxBuildingVisitor(style));
        using var lexer = BytesLexer.FromString(MarkdownMacros.ResolveBackslashEscapes(MarkdownMacros.NormalizeLatexAliases(source)), MarkdownMacros.MathLexerTable);
        using var tokens = new SyncLATokenIterator(lexer);
        var item = parser.ParseInput(tokens, debugger: null);
        return item.IsError || item.Content is not Func<BoxStyle, Box> builder ? null : BoxRasterizer.RenderToRgba(builder(style), style);
    }
    catch
    {
        return null;
    }
}

static Assembly LoadBaseline(string repo, string sha)
{
    var dir = Path.Combine(repo, "tools", "MathPerf", "obj", "baseline", sha[..12]);
    var output = Path.Combine(dir, "out");
    var dll = Path.Combine(output, "Console.Lib.dll");
    if (!File.Exists(dll))
    {
        Out.Error.WriteLine($"building Console.Lib at {sha[..7]} into {dir} (once per baseline)...");
        Directory.CreateDirectory(dir);
        var tar = Path.Combine(dir, "src.tar");
        Run("git", repo, "archive", "--format=tar", "-o", tar, sha,
            "src/Console.Lib", "src/Directory.Build.props", "src/Directory.Packages.props");
        TarFile.ExtractToDirectory(tar, dir, overwriteFiles: true);
        // Against the DIR.Lib and codec packages, as CI builds it: the archive has no siblings to find.
        Run("dotnet", dir, "build", Path.Combine(dir, "src", "Console.Lib", "Console.Lib.csproj"),
            "-c", "Release", "-o", output, "-nologo", "-v:q",
            "-p:UseLocalDirLib=false", "-p:UseLocalSharpAstroCodecs=false", "-p:GeneratePackageOnBuild=false");
    }

    // Only Console.Lib is loaded here; its references fall through to the default context, so DIR.Lib is
    // the one instance both sides share.
    return new AssemblyLoadContext($"Console.Lib@{sha[..7]}").LoadFromAssemblyPath(dll);
}

// Runs a tool without a window of its own, and throws with its output when it fails.
static string Run(string exe, string workingDirectory, params string[] arguments)
{
    var start = new ProcessStartInfo(exe)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {exe}");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{exe} {string.Join(' ', arguments)} exited {process.ExitCode}:\n{stderr.Result}{stdout.Result}");
    return stdout.Result;
}

/// <summary>One Console.Lib's entry points, as delegates, so the two sides time through the same shape.</summary>
sealed record Side(
    string Name,
    Func<string, int, BoxRenderMode, List<string>> Render,
    Func<RgbaImage, byte[]> EncodeBytes,
    Func<RgbaImage, string> EncodeImage)
{
    public static Side FromWorkingTree(string name, string font) => new(
        name,
        (markdown, width, mode) => MarkdownRenderer.RenderLines(markdown, width, ColorMode.TrueColor, null, mode, font),
        image =>
        {
            using var stream = new MemoryStream();
            SixelEncoder.Encode(image.Pixels, image.Width, image.Height, 4, stream);
            return stream.ToArray();
        },
        image =>
        {
            using var writer = new StringWriter();
            BoxRenderer.EncodeImage(image.Pixels, image.Width, image.Height, BoxRenderMode.Sixel, writer);
            return writer.ToString();
        });

    public static Side FromBaseline(string name, Assembly assembly, string font)
    {
        Type Get(string type) => assembly.GetType(type) ?? throw new InvalidOperationException($"{type} is not in the baseline Console.Lib");
        var colorMode = Get("Console.Lib.ColorMode");
        var boxRenderMode = Get("Console.Lib.BoxRenderMode");
        var trueColor = Enum.Parse(colorMode, nameof(ColorMode.TrueColor));
        var modes = Enum.GetValues<BoxRenderMode>().ToDictionary(m => m, m => Enum.Parse(boxRenderMode, m.ToString()));

        var renderLines = Get("Console.Lib.MarkdownRenderer").GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "RenderLines" && m.GetParameters() is { Length: 8 } p && p[2].ParameterType == colorMode);
        var encode = (Get("Console.Lib.SixelEncoder").GetMethod("Encode", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("SixelEncoder.Encode is not in the baseline")).CreateDelegate<SixelEncode>();
        var encodeImage = Get("Console.Lib.BoxRenderer").GetMethod("EncodeImage", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("BoxRenderer.EncodeImage is not in the baseline");

        return new(
            name,
            (markdown, width, mode) => renderLines.Invoke(null, [markdown, width, trueColor, null, modes[mode], font, null, null]) as List<string>
                ?? throw new InvalidOperationException("baseline RenderLines returned no list"),
            image =>
            {
                using var stream = new MemoryStream();
                encode(image.Pixels, image.Width, image.Height, 4, stream, default);
                return stream.ToArray();
            },
            image =>
            {
                using var writer = new StringWriter();
                encodeImage.Invoke(null, [image.Pixels, image.Width, image.Height, modes[BoxRenderMode.Sixel], writer]);
                return writer.ToString();
            });
    }
}

delegate void SixelEncode(byte[] rawPixels, int width, int height, int channels, Stream output, ReadOnlySpan<RGBAColor32> reserved);
