# `\ce{...}` — mhchem Phase-1 in Console.Lib

`Console.Lib.MarkdownRenderer` recognises `\ce{...}` inside any math span (`\(...\)`, `$...$`, `\[...\]`, `$$...$$`) and pre-bakes the body to single-line Unicode before the LaTeX grammar runs. The renderer is a deliberate Phase-1 subset of [mhchem](https://www.ctan.org/pkg/mhchem) — enough for the vast majority of textbook chemistry markup, *not* a drop-in replacement for the full LaTeX package.

Implementation: hand-rolled state machine in `src/Console.Lib/Mhchem.cs` (~270 lines). Not LALR — chemistry markup is heavily context-sensitive (`H2` vs `2H`, `CO` vs `Co`, leading `^{N}` vs postfix `^N`) in ways an LALR(1) grammar handles poorly. KaTeX, MathJax, and the original Hensel implementations all use state machines for the same reason.

## Supported (Phase 1)

| Feature | Example | Renders as |
|---|---|---|
| Element symbols (118 elements) | `\ce{Fe}` | Fe |
| Auto-subscript digits | `\ce{H2O}`, `\ce{CaCO3}` | H₂O, CaCO₃ |
| Two-letter symbol disambiguation | `\ce{CO2}` vs `\ce{Co2O3}` | CO₂ (C+O+2) vs Co₂O₃ (Co+2+O+3) |
| Coefficients | `\ce{2H2 + O2}` | 2H₂ + O₂ |
| Parenthesised groups with trailing subscript | `\ce{Ca(OH)2}`, `\ce{(NH4)2SO4}` | Ca(OH)₂, (NH₄)₂SO₄ |
| State markers | `\ce{H2O(l)}`, `\ce{HCl(aq)}` | H₂O(l), HCl(aq) — parens pass through verbatim |
| Isotope-prefix mass number | `\ce{^{238}U}` | ²³⁸U |
| Isotope with atomic number | `\ce{^{14}_{6}C}` | ¹⁴₆C |
| Ion charges (sign only) | `\ce{Na^+}`, `\ce{OH^-}` | Na⁺, OH⁻ |
| Ion charges (magnitude + sign) | `\ce{Fe^3+}`, `\ce{Cu^{2+}}`, `\ce{SO4^{2-}}` | Fe³⁺, Cu²⁺, SO₄²⁻ |
| Forward reaction | `\ce{A -> B}` | A → B |
| Reverse reaction | `\ce{A <- B}` | A ← B |
| Equilibrium | `\ce{N2 + 3H2 <=> 2NH3}` | N₂ + 3H₂ ⇌ 2NH₃ |
| Reversible (resonance) | `\ce{A <-> B}` | A ↔ B |
| Plus separators | `\ce{H2 + Cl2 -> 2HCl}` | H₂ + Cl₂ → 2HCl |

## Out of scope (Phase 2+)

Each of these falls through as verbatim text — they don't *break* anything, they just don't get any special treatment. If you need them, write the Unicode yourself or wait for a follow-up phase.

| Not supported | What you'd hope for | What you get today |
|---|---|---|
| Single bonds | `\ce{H-O-H}` (intended as H–O–H) | `H-O-H` literal — `-` is plain unless followed by `>` (arrow) |
| Double/triple bonds | `\ce{O=O}`, `\ce{N#N}` | `O=O`, `N#N` literal |
| Labelled arrows | `\ce{A ->[catalyst] B}` | Square-bracket label leaks through after the arrow |
| Arrows with stacked above/below labels | `\ce{A ->[\Delta][\text{slow}] B}` | Same — labels leak through |
| `$...$` escape hatch (real math inside) | `\ce{$\alpha$-D-glucose}` | `$α$-D-glucose` literal-ish — dollar signs aren't escape syntax here |
| Electron arrows (Lewis pair flow) | `\ce{A <-> B}` was used historically for electron pair; we render it as resonance (↔) | Disambiguating Lewis ↔ resonance is mhchem's call; we picked resonance |
| Charge stacking (`X^2+_3` — charge over coefficient) | `\ce{SO4^{2-}_3}` | Renders `SO₄²⁻₃` (side-by-side), not vertically stacked — terminal text can't stack on the same column anyway |
| Sideset / atomic-orbital sub-sup pairs | `\ce{_{}^{14}C}` | The leading empty `_{}` becomes literal `_{}` text |
| Multi-line equations | `\ce{...\\...}` | Newline character passes through but no alignment |
| `\ce{...}` outside math spans | `Just \ce{H2O} in prose.` | The `\ce{H2O}` survives verbatim — currently the macro is recognised only inside `\(..\)` / `$..$` / `\[..\]` / `$$..$$` because the hook lives in `ExpandLatexMacros`. Wrap in `\(...\)`. |
| Bond-line structures | `\ce{H-C(=O)-OH}` (acetic acid skeletal) | Bond chars leak through |
| Nested formulas | `\ce{[Cu(NH3)4]^{2+}}` | The outer `[...]` survives verbatim (not a recognised group delimiter — only `(...)` are) |

## Graceful degradation

The renderer never throws. Unknown / unsupported tokens fall through as plain text, so:

- `\ce{abc}` → `abc` (no known symbols, lowercase passes through)
- `\ce{X^{abc}}` → `X^{abc}` (script body with non-mappable letters falls back to the literal `^{...}` form so the author can see what they wrote)
- `\ce{H2 - O2}` → `H₂ - O₂` (bare `-` not followed by `>` stays literal)

## Integration points

- **Hook**: `MarkdownRenderer.ExpandLatexMacros` (line ~199 in `MarkdownRenderer.cs`), next to `\text{}` and `\boxed{}`. Same `ExpandBalancedMacro` mechanism.
- **Reach**: All four math forms (`\(..\)`, `$..$`, `\[..\]`, `$$..$$`) route through `RenderMathUnicode` → `ExpandLatexMacros`, so `\ce` works in any of them.
- **AOT**: `Mhchem.Render` is a pure managed function with no reflection; the 118-element lookup is a `FrozenSet<string>`. AOT-compatible.
- **Visibility**: `internal static class Mhchem` with `InternalsVisibleTo("Console.Lib.Tests")` so the tests can call `Render(string)` directly without going through the markdown pipeline.

## Upgrade path

The most-requested Phase-2 additions, in rough effort order:

1. **`\ce` outside math spans** — move the `ExpandBalancedMacro(source, "ce", ...)` pass to run on the raw markdown source (before paragraph parsing) instead of inside `ExpandLatexMacros`. ~10 lines + tests. Lets `\ce{H2O}` work directly in prose.
2. **Labelled arrows** `\ce{A ->[cat] B}` — extend `TryArrow` to consume an optional `[...]` after the arrow and render it as a superscripted Unicode run above the arrow glyph (e.g. `→ᶜᵃᵗ`). ~40 lines + tests.
3. **Single/double/triple bonds** as printed dashes (`H–O–H`, `O═O`, `N≡N`). Needs disambiguating `-` from "minus in front of digit" and from "start of arrow". ~30 lines + tests.
4. **`$...$` escape hatch** — let the body include math segments that flow back through the LaTeX grammar. Requires teaching `Mhchem.Render` to recognise `$..$` and pass those segments through `MarkdownRenderer.RenderMathUnicode`, splicing the result back. Some recursion risk; needs careful test coverage. ~80 lines + tests.

Anything past that (bond lines, charge stacking, multi-line alignments, sideset) starts being the long-tail mhchem features that KaTeX's `mhchemParser.js` spends most of its 1700 lines on. Worth doing iff there's a real consumer asking for them.
