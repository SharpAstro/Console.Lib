using System;
using System.Collections.Generic;
using System.Text;
using DIR.Lib.MathLayout;
using LALR.CC.LexicalGrammar;

namespace Console.Lib;

/// <summary>
/// Phase-B visitor over the <c>markdown-inline.lalr.yaml</c> grammar.
/// Produces an <see cref="IReadOnlyList{MdInline}"/> by composing per-span
/// builders; for every math form, the rewriter invokes the existing
/// <see cref="Latex"/> parser as a sub-parser on the captured body string
/// and stores the resulting Unicode rendering on <see cref="MdMathInline"/>.
///
/// <para><b>Sub-parser pattern.</b> Symbol-ID spaces stay disjoint because
/// the LaTeX parser is a separate <see cref="LALR.CC.Parser"/> instance;
/// communication is one-way via the visitor's return value stored on
/// <c>Item.Content</c>.</para>
///
/// <para><b>What's covered:</b> plain text, <c>$..$</c>, <c>$$..$$</c>,
/// <c>\(..\)</c>, <c>\[..\]</c>, <c>\boxed{..}</c> (with balanced braces
/// via the recursive <c>BoxedBody</c> rule). Phase B-extension will add
/// inline code, emphasis, links, line breaks, and color inlines.</para>
/// </summary>
internal sealed class MarkdownInlineVisitor : MarkdownInline.IVisitor<object>
{
    /// <summary>Parses an inline-only markdown string and returns the
    /// produced span list. Returns an empty list on parse error so the
    /// caller can fall through to a literal-text render of the source.</summary>
    public IReadOnlyList<MdInline> Parse(string source)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<MdInline>();

        try
        {
            using var lexer = global::LALR.CC.LexicalGrammar.BytesLexer.FromString(source, s_lexerTable);
            using var tokens = new global::LALR.CC.LexicalGrammar.SyncLATokenIterator(lexer);
            var result = s_parser.ParseInput(tokens, debugger: null);
            if (result.IsError) return Array.Empty<MdInline>();
            return result.Content as IReadOnlyList<MdInline> ?? Array.Empty<MdInline>();
        }
        catch (global::LALR.CC.ParseErrorException)
        {
            return Array.Empty<MdInline>();
        }
    }

    // ── Span-list assembly (epsilon-base + cons recursion) ────────────

    public object Visit(MarkdownInline.SpansEmpty node) =>
        (IReadOnlyList<MdInline>)Array.Empty<MdInline>();

    public object Visit(MarkdownInline.SpansCons node)
    {
        var head = (MdInline)node.Arg0.Content;
        var tail = (IReadOnlyList<MdInline>)node.Arg1.Content;
        var list = new List<MdInline>(tail.Count + 1) { head };
        list.AddRange(tail);
        return (IReadOnlyList<MdInline>)list;
    }

    // ── Plain text ────────────────────────────────────────────────────

    public object Visit(MarkdownInline.LiteralSpan node) =>
        new MdLiteral((string)node.Arg0.Content);

    // ── Inline code (single-backtick fences) ─────────────────────────

    public object Visit(MarkdownInline.CodeSpan node) =>
        new MdCodeInline((string)node.Arg1.Content);

    // ── Math: dollar forms ($..$, $$..$$) ─────────────────────────────

    public object Visit(MarkdownInline.MathSpan node) =>
        BuildMath((string)node.Arg1.Content);

    public object Visit(MarkdownInline.MathDisplaySpan node) =>
        BuildMath((string)node.Arg1.Content);

    // ── Math: LaTeX-backslash forms (\(..\), \[..\]) ──────────────────

    public object Visit(MarkdownInline.MathParenSpan node) =>
        BuildMath((string)node.Arg1.Content);

    public object Visit(MarkdownInline.MathBracketSpan node) =>
        BuildMath((string)node.Arg1.Content);

    // ── Math: \boxed{..} with balanced braces ─────────────────────────
    //
    // Body is captured as a balanced-brace structure (BoxedBody). The
    // sub-parser invocation wraps the body in `\boxed{X}` again so the
    // existing math pipeline's \boxed handler (frame in Unicode mode,
    // strip in box mode) sees the macro it expects.

    public object Visit(MarkdownInline.MathBoxedSpan node)
    {
        // Source = the body alone (consistent with the other math forms,
        // where the wrapper delimiters are not part of `Source`). The
        // sub-parser still has to see the full `\boxed{X}` so its own
        // \boxed handler kicks in — we re-wrap just for that call.
        var body = (string)node.Arg1.Content;
        return new MdMathInline(
            Source: body,
            Unicode: ParseMathUnicode("\\boxed{" + body + "}"),
            Builder: null);
    }

    // ── LatexBody assembly: concat frags ──────────────────────────────

    public object Visit(MarkdownInline.LatexBodyEmpty node) => string.Empty;

    public object Visit(MarkdownInline.LatexBodyCons node)
    {
        var head = (string)node.Arg0.Content;
        var tail = (string)node.Arg1.Content;
        return head + tail;
    }

    // ── BoxedBody assembly: items can be frags or { nested } groups ──

    public object Visit(MarkdownInline.BoxedBodyEmpty node) => string.Empty;

    public object Visit(MarkdownInline.BoxedBodyCons node)
    {
        var head = (string)node.Arg0.Content;
        var tail = (string)node.Arg1.Content;
        return head + tail;
    }

    public object Visit(MarkdownInline.BoxedItemFrag node) =>
        (string)node.Arg0.Content;

    public object Visit(MarkdownInline.BoxedItemGroup node)
    {
        // Preserve the literal `{ inner }` so the LaTeX sub-parser sees
        // it as the grouping construct it is.
        var inner = (string)node.Arg1.Content;
        return "{" + inner + "}";
    }

    // ── LaTeX sub-parser invocation ───────────────────────────────────

    private static MdMathInline BuildMath(string body) =>
        new(Source: body,
            Unicode: ParseMathUnicode(body),
            Builder: null);

    private static readonly LatexUnicodeVisitor s_unicodeVisitor = new();
    private static readonly global::LALR.CC.Parser s_unicodeParser = Latex.BuildParser(s_unicodeVisitor);
    private static readonly Dictionary<string, LexRule[]> s_mathLexerTable = Latex.BuildLexer();

    private static string ParseMathUnicode(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        try
        {
            using var lexer = global::LALR.CC.LexicalGrammar.BytesLexer.FromString(source, s_mathLexerTable);
            using var tokens = new global::LALR.CC.LexicalGrammar.SyncLATokenIterator(lexer);
            var item = s_unicodeParser.ParseInput(tokens, debugger: null);
            if (item.IsError) return source;
            return (item.Content as string) ?? source;
        }
        catch
        {
            return source;
        }
    }

    // ── Parser construction ───────────────────────────────────────────

    private static readonly global::LALR.CC.Parser s_parser =
        MarkdownInline.BuildParser(new MarkdownInlineVisitor());

    private static readonly Dictionary<string, LexRule[]> s_lexerTable =
        MarkdownInline.BuildLexer();
}
