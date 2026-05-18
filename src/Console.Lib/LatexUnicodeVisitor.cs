using System.Collections.Generic;
using System.Text;
using LALR.CC.LexicalGrammar;
using static Console.Lib.Latex;

namespace Console.Lib;

/// <summary>
/// IVisitor&lt;string&gt; for the math-mode LaTeX grammar: walks each AST node
/// to a plain-Unicode string suitable for inline rendering inside a single
/// terminal row. Used by <see cref="MarkdownRenderer"/> for Markdig's
/// <c>MathInline</c> nodes (single-dollar <c>$x^2$</c> / inline <c>\(...\)</c>
/// math); the box-rendered counterpart <see cref="BoxBuildingVisitor"/>
/// handles <c>MathBlock</c> nodes (double-dollar <c>$$...$$</c> / display
/// <c>\[...\]</c> math) where multi-row pixel output is acceptable.
///
/// Strategy: Greek letters render as Greek letters, common digits use
/// Unicode super/subscript codepoints when available, fractions use the
/// fraction slash (U+2044). Anything that doesn't have a Unicode form falls
/// back to caret/underscore notation, so e.g. <c>x^{a+b}</c> reads as
/// <c>x^(a + b)</c> rather than mangling into broken super/subscript runs.
///
/// Lifted from the LALR.CC Examples.Latex renderer.
/// </summary>
internal sealed class LatexUnicodeVisitor : IVisitor<string>
{
    public string Visit(Add node)      => $"{node.Arg0.Content} + {node.Arg2.Content}";
    public string Visit(Subtract node) => $"{node.Arg0.Content} − {node.Arg2.Content}";
    public string Visit(Eq node)       => $"{node.Arg0.Content} = {node.Arg2.Content}";
    public string Visit(Mul node)      => $"{node.Arg0.Content}·{node.Arg2.Content}";
    public string Visit(Div node)      => $"{node.Arg0.Content}/{node.Arg2.Content}";
    public string Visit(Juxt node)     => $"{node.Arg0.Content}{node.Arg1.Content}";
    public string Visit(Neg node)      => $"−{node.Arg1.Content}";

    public string Visit(Sup node) =>
        TryUnicodeScript((string)node.Arg2.Content, Superscripts, out var sup)
            ? $"{node.Arg0.Content}{sup}"
            : $"{node.Arg0.Content}^{Wrap((string)node.Arg2.Content)}";

    public string Visit(Subscript node) =>
        TryUnicodeScript((string)node.Arg2.Content, Subscripts, out var sub)
            ? $"{node.Arg0.Content}{sub}"
            : $"{node.Arg0.Content}_{Wrap((string)node.Arg2.Content)}";

    public string Visit(Number node)   => (string)node.Arg0.Content;
    public string Visit(Variable node) => (string)node.Arg0.Content;
    public string Visit(Command node)  => RenderCommand((string)node.Arg0.Content);

    public string Visit(Paren node) => $"({node.Arg1.Content})";
    public string Visit(Group node) => (string)node.Arg1.Content;

    public string Visit(Sqrt node) => $"√{Wrap((string)node.Arg1.Content)}";
    public string Visit(Frac node) => $"{Wrap((string)node.Arg1.Content)}⁄{Wrap((string)node.Arg2.Content)}";

    private static string Wrap(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Length == 1) return s;
        foreach (var c in s)
        {
            if (c == ' ' || c == '+' || c == '−' || c == '=' || c == '/' || c == '·' || c == '⁄')
                return $"({s})";
        }
        return s;
    }

    private static bool TryUnicodeScript(string s, IReadOnlyDictionary<char, char> table, out string? mapped)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 4)
        {
            mapped = null;
            return false;
        }
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!table.TryGetValue(c, out var sc))
            {
                mapped = null;
                return false;
            }
            sb.Append(sc);
        }
        mapped = sb.ToString();
        return true;
    }

    private static string RenderCommand(string raw)
    {
        if (raw.Length < 2 || raw[0] != '\\') return raw;
        var name = raw.Substring(1);
        return Commands.TryGetValue(name, out var glyph) ? glyph : raw;
    }

    private static readonly Dictionary<string, string> Commands = new(System.StringComparer.Ordinal)
    {
        // Lowercase Greek
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
        ["epsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ",
        ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ",
        ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ",
        ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ",
        ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        // Uppercase Greek (ones that aren't Latin lookalikes)
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ",
        ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ",
        ["Psi"] = "Ψ", ["Omega"] = "Ω",
        // Function names — kept as letters, render upright in fixed-width fonts.
        ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan",
        ["sec"] = "sec", ["csc"] = "csc", ["cot"] = "cot",
        ["arcsin"] = "arcsin", ["arccos"] = "arccos", ["arctan"] = "arctan",
        ["sinh"] = "sinh", ["cosh"] = "cosh", ["tanh"] = "tanh",
        ["log"] = "log", ["ln"] = "ln", ["exp"] = "exp",
        ["lim"] = "lim", ["max"] = "max", ["min"] = "min",
        ["det"] = "det", ["dim"] = "dim", ["gcd"] = "gcd",
        // Big operators
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["oint"] = "∮",
        ["bigcup"] = "⋃", ["bigcap"] = "⋂",
        // Constants and relation symbols
        ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇",
        ["pm"] = "±", ["mp"] = "∓", ["to"] = "→",
        ["leftarrow"] = "←", ["rightarrow"] = "→",
        ["leq"] = "≤", ["geq"] = "≥", ["neq"] = "≠",
        ["approx"] = "≈", ["equiv"] = "≡",
        ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂",
        ["cup"] = "∪", ["cap"] = "∩",
        // Arithmetic operators that the model often emits as commands inside
        // math (\cdot and \times are also lexer-aliased to '*' but the model
        // may end up here too).
        ["div"] = "÷", ["cdot"] = "·",
        // Spacing macros — render as plain spaces so juxtaposed atoms don't
        // run together when the model used them as visual separators.
        ["quad"] = "  ", ["qquad"] = "    ",
    };

    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³',
        ['4'] = '⁴', ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷',
        ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾',
        ['n'] = 'ⁿ', ['i'] = 'ⁱ',
    };

    private static readonly Dictionary<char, char> Subscripts = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃',
        ['4'] = '₄', ['5'] = '₅', ['6'] = '₆', ['7'] = '₇',
        ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎',
        // Lowercase letters with Unicode subscript forms (partial coverage).
        ['a'] = 'ₐ', ['e'] = 'ₑ', ['h'] = 'ₕ', ['i'] = 'ᵢ',
        ['j'] = 'ⱼ', ['k'] = 'ₖ', ['l'] = 'ₗ', ['m'] = 'ₘ',
        ['n'] = 'ₙ', ['o'] = 'ₒ', ['p'] = 'ₚ', ['r'] = 'ᵣ',
        ['s'] = 'ₛ', ['t'] = 'ₜ', ['u'] = 'ᵤ', ['v'] = 'ᵥ', ['x'] = 'ₓ',
    };
}
