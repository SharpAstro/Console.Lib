using System;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace Console.Lib;

/// <summary>
/// Markdig inline parser that recognises LaTeX backslash math forms the
/// stock <c>UseMathematics()</c> extension does not handle — emitting them
/// as proper <see cref="MathInline"/> AST nodes during parse instead of
/// requiring a pre-pass to rewrite them into <c>$…$</c> form. Triggers on
/// <c>\</c> and consumes one of:
///
/// <list type="bullet">
///   <item><c>\(X\)</c> — inline math. The body is captured verbatim and
///   handed to the same math pipeline that processes <c>$X$</c>.</item>
///   <item><c>\boxed{X}</c> — math-benchmark-trained models (Qwen-Math,
///   DeepSeek-R1, etc.) emit <c>\boxed{…}</c> as a top-level final-answer
///   callout without bothering to wrap it in math delimiters. We treat the
///   entire <c>\boxed{X}</c> token as the math inline so the math pipeline's
///   own <c>\boxed</c> handler (which already exists for the wrapped case)
///   takes over — no special-case rendering here.</item>
/// </list>
///
/// <para>The block-level <c>\[X\]</c> form is not handled here — it needs a
/// <see cref="BlockParser"/>, which is a bigger surface than this inline
/// parser. <c>PreProcessLatexWrappers</c> keeps its single-line
/// <c>\[…\] → $$…$$</c> regex for now; once a block parser exists that
/// regex can go away too.</para>
///
/// <para>Anything else starting with <c>\</c> (loose <c>\div</c>, <c>\alpha</c>,
/// random escapes) returns <c>false</c> so other parsers — Markdig's
/// builtin escape handling, the prose-Unicode pass — get their shot.</para>
/// </summary>
public sealed class LatexBackslashInlineParser : InlineParser
{
    public LatexBackslashInlineParser()
    {
        OpeningCharacters = ['\\'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var start = slice.Start;
        var next = slice.PeekChar();

        if (next == '(')
            return TryMatchParenWrapped(processor, ref slice, start);

        if (next == 'b' && LooksLike(slice, "\\boxed"))
            return TryMatchBoxed(processor, ref slice, start);

        return false;
    }

    /// <summary>
    /// Match <c>\(...\)</c>. Walks forward looking for the closing <c>\)</c>;
    /// bails on EOF or newline (LaTeX inline math is single-line by convention).
    /// On match, emits a <see cref="MathInline"/> whose <c>Content</c> spans
    /// just the body — no <c>\(</c> / <c>\)</c> delimiters.
    /// </summary>
    private static bool TryMatchParenWrapped(InlineProcessor processor, ref StringSlice slice, int start)
    {
        var text = slice.Text;
        int bodyStart = start + 2; // skip past "\("
        int i = bodyStart;
        int end = slice.End;
        while (i < end)
        {
            char c = text[i];
            if (c == '\n' || c == '\r') return false;
            if (c == '\\' && i + 1 <= end && text[i + 1] == ')')
            {
                EmitMath(processor, ref slice, start, i + 2, bodyStart, i, delimiter: '(', delimiterCount: 1);
                return true;
            }
            i++;
        }
        return false;
    }

    /// <summary>
    /// Match <c>\boxed{X}</c> with balanced-brace scanning over the body.
    /// The emitted <see cref="MathInline.Content"/> includes the literal
    /// <c>\boxed{...}</c> bytes — the math pipeline's macro expansion handles
    /// the rest, same as if the model had written <c>$\boxed{X}$</c>.
    /// </summary>
    private static bool TryMatchBoxed(InlineProcessor processor, ref StringSlice slice, int start)
    {
        var text = slice.Text;
        int end = slice.End;
        int i = start + "\\boxed".Length;

        // Tolerate "\boxed  {" — LaTeX itself does.
        while (i <= end && text[i] == ' ') i++;
        if (i > end || text[i] != '{') return false;

        int depth = 0;
        for (int j = i; j <= end; j++)
        {
            char c = text[j];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    EmitMath(processor, ref slice, start, j + 1, start, j + 1, delimiter: '\\', delimiterCount: 1);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Peek-only check that <paramref name="slice"/> starts with
    /// <paramref name="prefix"/>. Does not advance the slice.
    /// </summary>
    private static bool LooksLike(StringSlice slice, string prefix)
    {
        var text = slice.Text;
        int start = slice.Start;
        int end = slice.End;
        if (end - start + 1 < prefix.Length) return false;
        for (int k = 0; k < prefix.Length; k++)
            if (text[start + k] != prefix[k]) return false;
        return true;
    }

    /// <summary>
    /// Common emission path. Advances <paramref name="slice"/> past the end
    /// of the consumed region and builds a <see cref="MathInline"/> whose
    /// content slice points at <c>[bodyStart, bodyEnd)</c>.
    /// </summary>
    private static void EmitMath(InlineProcessor processor, ref StringSlice slice,
        int matchStart, int matchEnd, int bodyStart, int bodyEnd,
        char delimiter, int delimiterCount)
    {
        var content = new StringSlice(slice.Text, bodyStart, bodyEnd - 1);

        int line, column;
        processor.Inline = new MathInline
        {
            Span = new SourceSpan(
                processor.GetSourcePosition(matchStart, out line, out column),
                processor.GetSourcePosition(matchEnd - 1)),
            Line = line,
            Column = column,
            Delimiter = delimiter,
            DelimiterCount = delimiterCount,
            Content = content,
        };
        slice.Start = matchEnd;
    }
}

/// <summary>
/// Markdig extension that registers <see cref="LatexBackslashInlineParser"/>
/// alongside the builtin <see cref="MathInlineParser"/>. Adds support for
/// <c>\(...\)</c> and <c>\boxed{...}</c> without touching the markdown
/// source — Markdig sees them as math inlines directly.
/// </summary>
public sealed class LatexBackslashInlineExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<LatexBackslashInlineParser>())
        {
            // Register before the default escape handler so our \( and \boxed
            // get first crack at the \ trigger character.
            pipeline.InlineParsers.Insert(0, new LatexBackslashInlineParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer) { }
}

internal static class LatexBackslashInlineExtensions
{
    public static MarkdownPipelineBuilder UseLatexBackslashInline(this MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.Extensions.Contains<LatexBackslashInlineExtension>())
            pipeline.Extensions.Add(new LatexBackslashInlineExtension());
        return pipeline;
    }
}
