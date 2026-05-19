# CLAUDE.md

## Build & Test

```bash
dotnet build src/Console.Lib
dotnet test src/Console.Lib.Tests
```

## Versioning

The project uses **SemVer** (`Major.Minor.Patch`) with CI-generated patch numbers. The major/minor part is maintained manually; the patch and metadata are injected by CI.

### How CI composes the NuGet version

The workflow (`.github/workflows/dotnet.yml`) defines:

```
VERSION_PREFIX  = X.Y.<run_number>        # e.g. 2.0.47
VERSION_REV    = <run_attempt>            # e.g. 1
VERSION_HASH   = +<sha>                   # e.g. +a1b2c3d
```

These are passed to `dotnet build` as:
- **Package version** (`-p:Version`): `X.Y.<run>.<attempt>+<sha>` — the full SemVer+metadata string that appears on NuGet.
- **File version** (`-p:FileVersion`): `X.Y.<run>.<attempt>` — the Windows file version (no hash).

### Files to update when bumping the version

Three values across two files must stay in sync:

| File | Property | Format | Example |
|---|---|---|---|
| `src/Console.Lib/Console.Lib.csproj` | `<VersionPrefix>` | `X.Y.Z` | `2.0.0` |
| `src/Console.Lib/Console.Lib.csproj` | `<AssemblyVersion>` | `X.Y.0.0` | `2.0.0.0` |
| `.github/workflows/dotnet.yml` | `VERSION_PREFIX` | `X.Y.${{ github.run_number }}` | `2.0.${{ github.run_number }}` |

`VersionPrefix` is the local/debug package version. `AssemblyVersion` governs .NET assembly binding. `VERSION_PREFIX` drives the CI-published NuGet version. All three must share the same `X.Y` major/minor.

## DIR.Lib local-project reference

`Console.Lib.csproj` uses a conditional reference for **DIR.Lib**: if the sibling working copy `../DIR.Lib/src/DIR.Lib/DIR.Lib.csproj` exists on disk, MSBuild picks it up as a `ProjectReference` (great for in-tree iteration). Otherwise it falls back to the NuGet `PackageReference`. The switch is controlled by the `UseLocalDirLib` MSBuild property, which can also be set explicitly (e.g., `-p:UseLocalDirLib=false`) to force the package path.

## Layering: Console.Lib vs DIR.Lib.Markdown

As of v2.14 Console.Lib is the **terminal-rendering layer** only. The Markdown / LaTeX parser layer — LALR.CC grammars (`markdown-inline.lalr.yaml`, `markdown-block.lalr.yaml`, `latex.lalr.yaml`), the macro-expansion / `\ce{...}` mhchem state machine (`MarkdownMacros`, `Mhchem`), and the `BoxBuildingVisitor` that turns the LaTeX AST into a `Box` tree — all live in **DIR.Lib.Markdown**. The LALR.CC source generator + the `YamlDotNet` build-time dependency moved with them, so Console.Lib's csproj no longer carries either.

What stays here:

- `MarkdownRenderer` — walks the `MdBlock` / `MdInline` trees produced by DIR.Lib.Markdown and emits VT-styled lines (theme, word wrap, OSC 8 hyperlinks, SGR coloring, inline / display math dispatch).
- `BoxRenderer` — rasterises `Box` trees (from DIR.Lib.MathLayout) to Sixel / Unicode sextants / half-blocks for display math.
- `MarkdownWidget` — viewport-bound, scroll- and resize-aware wrapper around `MarkdownRenderer`.

What moved out (mention here so future-me doesn't go looking for it in this repo):

- `Mhchem.cs` → `DIR.Lib/src/DIR.Lib/Markdown/Mhchem.cs`
- The three `*.lalr.yaml` grammar files and the LALR.CC source-generator wiring
- `BoxBuildingVisitor` (`Latex.AstVisitor` subclass) — now public in `DIR.Lib.Markdown`
- The `Markdig` dependency was dropped entirely in 2.15-era cleanup (Phase F); nothing in Console.Lib references it anymore.

Bumping DIR.Lib past a minor that touches `DIR.Lib.Markdown` may require coordinated changes here — the `MarkdownRenderer` walks the public `MdBlock` / `MdInline` shape directly.

## Key design notes

- **Windows VT I/O** (`WindowsConsoleInput.EnableVirtualTerminalIO`) is only activated when entering alternate screen mode, not during `InitAsync()`. This keeps `Console.ReadKey` working correctly in normal (non-alternate) mode for ASCII/text-based UIs.
- **`TryReadInput`** uses `intercept: true` in normal mode — keystrokes are never echoed. Callers control display feedback (e.g., via `WriteInPlace`).
- **`ConsoleInputEvent.KeyChar`** is a `Rune?` populated from the raw stdin byte stream with UTF-8 continuation-byte buffering, so non-ASCII input (`é`, `中`, emoji) round-trips through widgets without going through the US-layout `InputKeyCharMapping` table. `TextArea.HandleChar` / `TextInputBar.HandleInput` consume the event directly.
- **`MenuBase<T>`** in normal mode shows a `> ` prompt and echoes the selected item on confirmation.
- **`ColorMode` enum** has a `None` value (ordinal 0) before `Sgr16` and `TrueColor`. Code that persisted or compared `ColorMode` by integer value may need updating. `ColorMode.None` suppresses all escape sequences for plain-text capture.
- **`MarkdownRenderer` math rendering** has three modes for display math (`$$...$$`, `\[...\]`) — Sixel (true raster), Unicode sextant (2×3 sub-cell blocks, no Sixel needed), and half-block (universal fallback). Inline math always renders as single-row Unicode. Callers pick the mode after probing terminal capability via DA1 (`HasSixelSupport`).
