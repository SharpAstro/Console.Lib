using System.Collections.Generic;
using DIR.Lib.MathLayout;

namespace Console.Lib;

/// <summary>
/// Phase-A spike: skeleton AST for the LALR.CC-based markdown renderer.
/// Replaces the Markdig <c>Inline</c> hierarchy that <see cref="MarkdownRenderer"/>'s
/// switch statement currently walks. Block-level types (<c>MdBlock</c>) land
/// in Phase C; this file is just enough for the inline-grammar spike to
/// produce something a future renderer can walk.
///
/// <para>All types are <c>record</c>s for value-equality semantics — handy
/// for AST diffing in tests, and the cost is negligible for read-mostly
/// tree walks.</para>
/// </summary>
public abstract record MdInline;

/// <summary>Plain text run. <see cref="Text"/> may contain whitespace and
/// punctuation; emphasis / code / link / math markers are stripped by the
/// grammar into their own <see cref="MdInline"/> subtypes before this is
/// emitted.</summary>
public sealed record MdLiteral(string Text) : MdInline;

/// <summary>An inline math span — anything between <c>$..$</c>,
/// <c>\(..\)</c>, or the bare <c>\boxed{..}</c> form once Phase B adds
/// those productions. <see cref="Source"/> is the raw LaTeX body (no
/// delimiters). <see cref="Unicode"/> is the rendering through
/// <see cref="LatexUnicodeVisitor"/>; <see cref="Builder"/> is the
/// deferred box-mode rasteriser produced by <see cref="BoxBuildingVisitor"/>
/// (null when the renderer is in Unicode-only mode).</summary>
public sealed record MdMathInline(
    string Source,
    string Unicode,
    System.Func<BoxStyle, Box>? Builder
) : MdInline;

/// <summary>Inline code span — text wrapped in single backticks. Emitted
/// for `` `code` `` patterns; the renderer paints <see cref="Content"/>
/// in the Code theme colour. Phase B handles single-backtick fences only;
/// CommonMark's multi-backtick fence (which lets single backticks appear
/// inside the body) lands in Phase B-extension.</summary>
public sealed record MdCodeInline(string Content) : MdInline;

/// <summary>Emphasis (italic / bold / bold-italic). <see cref="Level"/>
/// is 1 for <c>*italic*</c>, 2 for <c>**bold**</c>, 3 for
/// <c>***bold-italic***</c> (the pairing pass collapses adjacent
/// markers). The renderer maps level to VT bold + italic attributes.</summary>
public sealed record MdEmphasis(int Level, System.Collections.Generic.IReadOnlyList<MdInline> Content) : MdInline;

/// <summary>Transient emphasis-delimiter marker. Emitted by the grammar
/// for each `*` or `**` token; replaced by <see cref="MdEmphasis"/>
/// nodes during <see cref="MarkdownInlineVisitor.Parse"/>'s post-pass
/// pairing step. Any markers that survive the pairing (unmatched
/// delimiters like the `*` in <c>2 * 3 = 6</c>) are rewritten back to
/// <see cref="MdLiteral"/> so the rendered output shows the original
/// text rather than a stray placeholder. Should not normally appear
/// in the final span list returned to consumers.</summary>
internal sealed record MdStarMarker(int Level) : MdInline;

/// <summary>Transient container produced by the plain-bracket
/// production (<c>[text]</c> with no link/color tail). The
/// <see cref="MarkdownInlineVisitor.Parse"/> post-pass flattens
/// <see cref="MdGroup"/> nodes into their parent span list so the
/// final result is a flat sequence with no MdGroup wrappers. Useful
/// when a grammar production needs to emit multiple inlines but the
/// visitor surface returns a single MdInline.</summary>
internal sealed record MdGroup(System.Collections.Generic.IReadOnlyList<MdInline> Children) : MdInline;

/// <summary>Link inline — <c>[text](url)</c>. <see cref="Text"/> is the
/// link's display content (parsed as inline spans by the same grammar,
/// so the brackets can wrap bold/italic/math, etc.); <see cref="Url"/>
/// is the raw URL string from the parens body.</summary>
public sealed record MdLink(System.Collections.Generic.IReadOnlyList<MdInline> Text, string Url) : MdInline;

/// <summary>Color inline — Console.Lib extension syntax
/// <c>[text]{color}</c>. <see cref="Color"/> is the literal colour
/// string from the brace body (validated by the renderer against
/// <c>MarkdownTheme.TryParseColor</c>); invalid colours render as
/// plain text with the brackets and braces stripped. <see cref="Text"/>
/// is the bracketed inline content.</summary>
public sealed record MdColor(System.Collections.Generic.IReadOnlyList<MdInline> Text, string Color) : MdInline;

/// <summary>Line break — soft or hard per CommonMark. A soft break is
/// a lone newline inside a paragraph and renders as a single space; a
/// hard break is two-plus trailing spaces + newline (or trailing
/// <c>\\</c> + newline) and renders as an actual line terminator in
/// the output.</summary>
public sealed record MdLineBreak(bool Hard) : MdInline;
