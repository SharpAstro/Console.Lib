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
