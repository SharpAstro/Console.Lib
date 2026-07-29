# CLAUDE.md

## Build & Test

```bash
dotnet build src/Console.Lib
dotnet test src/Console.Lib.Tests
dotnet run --project src/MdCat -- README.md   # build & run the mdcat CLI in place
```

**mdcat** (`src/MdCat`) is a `cat`-for-Markdown CLI built on Console.Lib's `MarkdownRenderer`.
It ships two ways: a .NET global tool (NuGet, version melded to Console.Lib — see Versioning)
and per-platform native-AOT binaries (tag-driven via `mdcat-vX.Y.Z`, `mdcat-release.yml`). It
bundles STIX Two Math (`src/MdCat/Fonts/`) so display math renders without system fonts. See
`src/MdCat/README.md`.

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

**Two edits.** `<ConsoleLibVersion>` in `src/Directory.Build.props` is the single source of truth for the `X.Y`, and the workflow repeats it:

| File | Property | Format | Example |
|---|---|---|---|
| `src/Directory.Build.props` | `<ConsoleLibVersion>` | `X.Y` | `2.0` |
| `.github/workflows/dotnet.yml` | `VERSION_PREFIX` | `X.Y.${{ github.run_number }}` | `2.0.${{ github.run_number }}` |

Everything else derives from the first and must NOT be edited by hand:

| File | Property | Derives as |
|---|---|---|
| `src/Console.Lib/Console.Lib.csproj` | `<VersionPrefix>` | `$(ConsoleLibVersion).0` |
| `src/Console.Lib/Console.Lib.csproj` | `<AssemblyVersion>` | `$(ConsoleLibVersion).0.0` |
| `src/MdCat/MdCat.csproj` | `<VersionPrefix>` | `$(ConsoleLibVersion).0` |

`VersionPrefix` is the local/debug package version. `AssemblyVersion` governs .NET assembly binding. `VERSION_PREFIX` drives the CI-published NuGet version. All share the same `X.Y` by construction now — they did not before, and `AssemblyVersion` sat at `3.6.0.0` from 4.1 through 4.8 without anyone noticing, because nothing compares them.

The workflow's `VERSION_PREFIX` is the one value that cannot be derived: a workflow `env:` block cannot evaluate MSBuild. Making it derived means a computed step (`dotnet msbuild -getProperty:ConsoleLibVersion` into `$GITHUB_ENV`) instead of a static entry, which also has to relocate the release-note block living in that `env:`.

`src/Console.Lib.Inspector/Console.Lib.Inspector.csproj` keeps its own `<VersionPrefix>` (`1.0.0`) on purpose — it is a separately versioned MCP-server package. CI overrides it via the solution-wide `-p:Version` regardless.

The **mdcat** global tool's version is melded to Console.Lib's: CI passes the same `-p:Version` to the whole solution, and a dedicated `dotnet pack src/MdCat` step (mdcat is `PackAsTool`, so it can't use `GeneratePackageOnBuild`) publishes it to nuget.org alongside the library. Deriving its `VersionPrefix` keeps the LOCAL build in step too. (mdcat *binaries* — the native AOT GitHub Releases from `mdcat-release.yml` — are independent, tag-driven via `mdcat-vX.Y.Z`.)

## DIR.Lib local-project reference

`Console.Lib.csproj` uses a conditional reference for **DIR.Lib**: if the sibling working copy `../DIR.Lib/src/DIR.Lib/DIR.Lib.csproj` exists on disk, MSBuild picks it up as a `ProjectReference` (great for in-tree iteration). Otherwise it falls back to the NuGet `PackageReference`. The switch is controlled by the `UseLocalDirLib` MSBuild property, which can also be set explicitly (e.g., `-p:UseLocalDirLib=false`) to force the package path.

## Layering: Console.Lib vs DIR.Lib.Markdown

As of v2.14 Console.Lib is the **terminal-rendering layer** only. The Markdown / LaTeX parser layer — LALR.CC grammars (`markdown-inline.lalr.yaml`, `markdown-block.lalr.yaml`, `latex.lalr.yaml`), the macro-expansion / `\ce{...}` mhchem state machine (`MarkdownMacros`, `Mhchem`), and the `BoxBuildingVisitor` that turns the LaTeX AST into a `Box` tree — all live in **DIR.Lib.Markdown**. The LALR.CC source generator + the `YamlDotNet` build-time dependency moved with them, so Console.Lib's csproj no longer carries either.

What stays here:

- `MarkdownRenderer` — walks the `MdBlock` / `MdInline` trees produced by DIR.Lib.Markdown and emits VT-styled lines (theme, word wrap, OSC 8 hyperlinks, SGR coloring, inline / display math dispatch).
- `BoxRenderer` — rasterises `Box` trees (from DIR.Lib.MathLayout) to Sixel / Unicode sextants / half-blocks for display math.
- `MarkdownWidget` — viewport-bound, scroll- and resize-aware wrapper around `MarkdownRenderer`.
- `TextTable` + `BorderStyle` / `BorderChars` — the one place box-drawing junctions and column-width
  arithmetic live. `MarkdownRenderer` renders its tables through it rather than owning private
  helpers, so mdcat inherits the border vocabulary transitively. Terminal-only (it emits VT text),
  which is why it lives here and not in DIR.Lib alongside the surface-neutral layout engine.

What moved out (mention here so future-me doesn't go looking for it in this repo):

- `Mhchem.cs` → `DIR.Lib/src/DIR.Lib/Markdown/Mhchem.cs`
- The three `*.lalr.yaml` grammar files and the LALR.CC source-generator wiring
- `BoxBuildingVisitor` (`Latex.AstVisitor` subclass) — now public in `DIR.Lib.Markdown`
- The `Markdig` dependency was dropped entirely in 2.15-era cleanup (Phase F); nothing in Console.Lib references it anymore.

Bumping DIR.Lib past a minor that touches `DIR.Lib.Markdown` may require coordinated changes here — the `MarkdownRenderer` walks the public `MdBlock` / `MdInline` shape directly.

## Layering: shared layout engine (DIR.Lib)

The dock/box layout primitives are surface-neutral and live in **DIR.Lib**: `DockLayout<T>`
(the four-way edge arithmetic), `LayoutNode` / `LayoutContent` (the arrangeable tree, with
per-leaf `Hit` + `OnClick`), `LayoutEngine.Arrange` → `ArrangedNode<T>`, `IMeasureContext<T>`,
and the menu model/view pair `MenuModel` + `MenuLayout.BuildTree` (`MenuColors`). DIR.Lib also
has the pixel-surface `PixelMenuWidget<TSurface>`. Console.Lib supplies the **cell surface**:

- `CellMeasureContext : IMeasureContext<int>` — text width = char count (one row tall),
  design units round to whole cells. (Wide-char / East-Asian-width is a documented follow-up.)
- `CellLayout` — static cell painter that walks the *same* arranged tree the pixel painter
  uses (`CellLayout.Paint`) and a reverse-order `CellLayout.HitTest` mapping (col,row) → leaf
  `Hit` (firing `OnClick`).
- `MenuWidget` — cell-surface counterpart to `PixelMenuWidget<TSurface>`; wraps `MenuModel`
  + `MenuLayout` via `CellLayout`.
- `TerminalLayout` — now delegates the edge arithmetic to `DockLayout<int>` (cells), keeping
  only the terminal-specific safety clamp (a strip never exceeds remaining cells) + viewport wiring.

So a bump of DIR.Lib that touches `DockLayout` / `LayoutNode` / `MenuLayout` / `MenuModel` can
require coordinated changes in `CellLayout` / `MenuWidget` / `TerminalLayout`.

## Key design notes

- **Windows VT I/O** (`WindowsConsoleInput.EnableVirtualTerminalIO`) is only activated when entering alternate screen mode, not during `InitAsync()`. This keeps `Console.ReadKey` working correctly in normal (non-alternate) mode for ASCII/text-based UIs.
- **`TryReadInput`** uses `intercept: true` in normal mode — keystrokes are never echoed. Callers control display feedback (e.g., via `WriteInPlace`).
- **`ConsoleInputEvent.KeyChar`** is a `Rune?` populated from the raw stdin byte stream with UTF-8 continuation-byte buffering, so non-ASCII input (`é`, `中`, emoji) round-trips through widgets without going through the US-layout `InputKeyCharMapping` table. `TextArea.HandleChar` / `TextInputBar.HandleInput` consume the event directly.
- **`MenuBase<T>`** in normal mode shows a `> ` prompt and echoes the selected item on confirmation.
- **`ColorMode` enum** has a `None` value (ordinal 0) before `Sgr16` and `TrueColor`. Code that persisted or compared `ColorMode` by integer value may need updating. `ColorMode.None` suppresses all escape sequences for plain-text capture.
- **`MarkdownRenderer` math rendering** has three modes for display math (`$$...$$`, `\[...\]`) — Sixel (true raster), Unicode sextant (2×3 sub-cell blocks, no Sixel needed), and half-block (universal fallback). Inline math always renders as single-row Unicode. Callers pick the mode after probing terminal capability via DA1 (`HasSixelSupport`).
- **`MarkdownRenderer` image rendering** (`![alt](src)`) is opt-in via the `MarkdownImageOptions? images` parameter on `RenderLines` / `Render`. An image alone on its own line block-rasters via the **same** emit path as display math (`BoxRenderer.EncodeImage` — the encoder switch extracted so any RGBA buffer reuses it); an image mid-paragraph, or any image when `images` is null/unresolvable, renders as alt text (empty alt → file name). The `MdImage` node lives in **DIR.Lib.Markdown** (grammar `![` opener + `imageSpan` production mirroring `linkSpan`). Decoding uses **StbImageSharp** (PNG / baseline JPEG / BMP / GIF), already transitive via DIR.Lib → SharpAstro.Fonts — no new package, AOT-safe. The renderer never fetches: the host's `Resolver` maps a `src` to bytes (mdcat reads local files relative to the doc dir and returns null for `http(s)`/`data:` — **no network**).
