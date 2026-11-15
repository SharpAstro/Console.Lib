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

**One edit, plus the note.** `<VersionMajorMinor>` in `src/Directory.Build.props` is the single source of truth for the `X.Y`, and the matching entry goes in [CHANGELOG.md](CHANGELOG.md) at the repo root in the same commit — newest first, one `## Major.Minor` section each. (The notes used to live in a comment block in `.github/workflows/dotnet.yml`; nothing ever read them there, and they had grown to 414 of that file's 481 lines.)

| File | Property | Format | Example |
|---|---|---|---|
| `src/Directory.Build.props` | `<VersionMajorMinor>` | `X.Y` | `2.0` |

Everything else derives from it and must NOT be edited by hand:

| File | Property | Derives as |
|---|---|---|
| `src/Console.Lib/Console.Lib.csproj` | `<VersionPrefix>` | `$(VersionMajorMinor).0` |
| `src/Console.Lib/Console.Lib.csproj` | `<AssemblyVersion>` | `$(VersionMajorMinor).0.0` |
| `src/MdCat/MdCat.csproj` | `<VersionPrefix>` | `$(VersionMajorMinor).0` |
| `.github/workflows/dotnet.yml` | `VERSION_PREFIX` | resolved in a build step, `$(VersionMajorMinor).<run>` |

`VersionPrefix` is the local/debug package version. `AssemblyVersion` governs .NET assembly binding. `VERSION_PREFIX` drives the CI-published NuGet version. All share the same `X.Y` by construction now — they did not before, and `AssemblyVersion` sat at `3.6.0.0` from 4.1 through 4.8 without anyone noticing, because nothing compares them.

The workflow value used to be the exception, a static `env:` entry restating the `X.Y`, because a workflow `env:` block cannot evaluate MSBuild. It is no longer: the build job resolves it in a step (`dotnet msbuild src/Directory.Build.props -getProperty:VersionMajorMinor` into `$GITHUB_ENV`) before it stamps `-p:Version`, so CI cannot publish a version the packages disagree with. `-getProperty` only *evaluates* the file, so the step needs no restore and runs first; a property that fails to resolve fails the run rather than quietly publishing everything as `.<run>`. The release-note block stays in that `env:` because several entries contain a double hyphen, which XML forbids inside a comment — add the entry there when you bump the number here.

The property is called `VersionMajorMinor` rather than something Console.Lib-specific because **every SharpAstro repo now uses this same pattern under this same name** (DIR.Lib, SdlVulkan.Renderer, WebGl.Renderer, Codecs, Fonts.Lib, FITS.Lib, SER.Lib, Lzip.Lib, LAN.Lib, FC.SDK, QHYCCD.SDK, ZWOptical.SDK, TianWen.DAL). One name means one thing to grep for when checking whether a repo is on the pattern.

`src/Console.Lib.Inspector/Console.Lib.Inspector.csproj` keeps its own `<VersionPrefix>` (`1.0.0`) on purpose — it is a separately versioned MCP-server package. CI overrides it via the solution-wide `-p:Version` regardless.

The **mdcat** global tool's version is melded to Console.Lib's: CI passes the same `-p:Version` to the whole solution, and a dedicated `dotnet pack src/MdCat` step (mdcat is `PackAsTool`, so it can't use `GeneratePackageOnBuild`) publishes it to nuget.org alongside the library. Deriving its `VersionPrefix` keeps the LOCAL build in step too. (mdcat *binaries* — the native AOT GitHub Releases from `mdcat-release.yml` — are independent, tag-driven via `mdcat-vX.Y.Z`.)

The same version also stamps the **Console.Lib.Debug** twin: CI packs `src/Console.Lib` a second time with
`-c Debug -p:DebugTwin=true` (and `-p:GeneratePackageOnBuild=false`, so bin/Debug holds no second nupkg), because
the Release Console.Lib has no `#if DEBUG` types. Under `DebugTwin` the csproj forces the DIR.Lib package path and
references DIR.Lib.Debug instead of DIR.Lib; the `DIR.Lib` and `DIR.Lib.Debug` pins in `Directory.Packages.props`
move together. `tools/debug-twin-check` is a consumer with no ProjectReference that compiles against that package
only and fails if its restore pulled the plain DIR.Lib
(`dotnet build tools/debug-twin-check/DebugTwinCheck.csproj -c Debug`, after a twin pack into
`artifacts/debug-twin`; CI passes `-p:ConsoleLibDebugVersion=<stamped version>`).

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
- `CellInputSurface` + `CellInputRouter.Create` (5.13): the cell host's `IPixelWidget`. `CellLayout.Paint(viewport, arranged, surface, ...)`
  registers the tree's click regions, fields, shortcuts, list rows and `Scroll` viewports in cells (as float units), and
  the `InputRouter` it builds routes keys, Tab, focus, clicks and the wheel with no host routing. A list that declares
  its cursor (`Node.ListOf(cursor, listId)`, DIR.Lib 11.61) is stepped, activated and revealed by DIR.Lib's router itself
  (its `DeclaredList.Active` rule, not a copy here); the surface only answers `CollectClippedListRows` and `RevealListRow`,
  and `CellLayout.Paint` resolves `FocusBackground` against the nearest enclosing declared cursor, so two widgets' lists in
  one tree need no per-page glue. `CellInputSurface.ListCursor` (`HandleListKey`) remains the path for a tree that declares nothing. A painted declared modal (`Builder.Modal`) is recorded by the same walk through `Ui.NoteModalPainted` (5.24), its card taken from an Anchored Top's child, so a cell host needs no call for modal routing.
- `TerminalLayout` — now delegates the edge arithmetic to `DockLayout<int>` (cells), keeping
  only the terminal-specific safety clamp (a strip never exceeds remaining cells) + viewport wiring.

So a bump of DIR.Lib that touches `DockLayout` / `LayoutNode` / `MenuLayout` / `MenuModel` can
require coordinated changes in `CellLayout` / `MenuWidget` / `TerminalLayout`.

## Key design notes

- **Cell wireframe checks.** `CellWireframe.RecordCells` paints through `CellLayout.Paint` with a recorder and returns a `CellWireframeFrame`: DIR.Lib's `WireframeFrame` (cells as units, text with Box/Ink/Visible, opaque fills, regions) plus `CellFact`s (arranged rect, whether a fill wrote any cell, the text row and lengths, depth for the enclosing container). `CheckCells` returns DIR.Lib's findings from `Wireframe.Check` at tolerance 0 beside `CellRules` findings (`DoubledRow`, `VanishedFill`, `CutWithRoom`, `NothingRecorded`, `SolidStrip` (a filled node one row tall across its container's full width, empty and painted with whole cells, not the half-block thin line; `CellFact.ThinFill` records the half path), `EmptyRows` (a filled container whose content is all on one row but which is arranged taller than that row plus its own vertical padding; `CellFact.PaddingRows` carries the allowance), a Console.Lib flags enum selectable via `CellWireframeOptions.Rules`); `WireframeRules` is never cast into. `CellWireframe.Describe` joins both for one assertion message. `PaintFill`/`FillCells` return whether they wrote a cell, which is what the vanished-fill rule reads. Recording also happens at the `CellBuffer` write: `Record`/`RecordCells` take the buffer and observe it (`CellBuffer.WriteObserver`) for the paint, so cells a hosted widget writes from `drawFill` are recorded as text and fills, writes `Paint` already records are not doubled, and a frame that wrote cells but recorded nothing fails the `NothingRecorded` rule.

- **Declared borders.** `CellLayout.Paint` draws a node's `Node.BorderStyle` (DIR.Lib 11.68; DIR.Lib's type, so `Layout.BorderStyle`, not this repo's `BorderStyle` enum) with `PaintBorder`, after the node's fill and before its children, on the outer ring of the arranged rect the engine reserved (one cell per side). Glyphs are `BorderChars.For(Light)`, or `Rounded` when `Radius > 0`; a corner glyph needs both its sides, else the run of the side present continues; the pen is the border colour on the fill under it; alpha 0 paints nothing; the scroll clip applies. Because it paints after the fill, the arc replaces the quadrant clip of `ClipFilledCorners`. The recorder marks it with `CellFact.Bordered` and records no text or fill. **Joins:** `PaintBorder` only collects arms per cell (`BorderCells`; a vertical side draws up/down, a horizontal one left/right, a corner its two); `FlushBorders` runs at the end of the same `Paint`, adds the arm toward any neighbour drawing the opposite arm back, and picks the glyph for the union from `BorderChars` (corner, run, tee, cross). A rounded cell stays an arc only while a plain corner; a junction is the light tee/cross. A cell painted twice keeps the pen of the node painted last. Nothing reads glyphs back from the terminal.
- **PixelAuthored rows and block-glyph fills.** `CellMeasureContext.PixelAuthored` implements DIR.Lib's `MapExtent`: one line of content is one row whatever its pixel padding (vertical padding rounds down, horizontal gives at most one cell a side, an empty spacer under a cell adds nothing, a filled node thinner than a cell keeps one row). `CellLayout.Paint` then paints sub-cell fills with glyphs: a fill whose design height (own Fixed height, else the nearest Fixed ancestor's) is under 8 units is upper half blocks, and a plain Star child of a horizontal stack ends in an eighth-block glyph (U+258F..U+2589) at its exact share. Paint has no measure context, so it detects a pixel-authored arrangement by a Fixed extent holding more design units than the cells it arranged to (`IsPixelAuthored`); cell-authored trees never trip it and paint as before.
- **Windows VT I/O** (`WindowsConsoleInput.EnableVirtualTerminalIO`) is only activated when entering alternate screen mode, not during `InitAsync()`. This keeps `Console.ReadKey` working correctly in normal (non-alternate) mode for ASCII/text-based UIs.
- **`TryReadInput`** uses `intercept: true` in normal mode — keystrokes are never echoed. Callers control display feedback (e.g., via `WriteInPlace`).
- **`ConsoleInputEvent.KeyChar`** is a `Rune?` populated from the raw stdin byte stream with UTF-8 continuation-byte buffering, so non-ASCII input (`é`, `中`, emoji) round-trips through widgets without going through the US-layout `InputKeyCharMapping` table. `TextArea.HandleChar` / `TextInputBar.HandleInput` consume the event directly.
- **`MenuBase<T>`** in normal mode shows a `> ` prompt and echoes the selected item on confirmation.
- **Terminal title.** `ITerminalViewport.Title` sets the window title via OSC 2 (`VirtualTerminal.TitleTransition`, pure, so the byte stream is testable). The first set pushes the user's title (`CSI 22;0 t`), `DisposeAsync` pops it (`CSI 23;0 t`) iff a push was emitted; same title twice writes once, controls are stripped, and redirected output or `ColorMode.None` writes nothing.
- **Mouse tracking.** `EnterAlternateScreen` writes `?1000h`, `?1002h`, then `?1003h` (any-event, so motion arrives with no button held, which is hover) and `?1004h` (focus reporting), then `?1006h`; dispose reverses them. `VirtualTerminal.MouseTracking = MouseTracking.ButtonMotion` opts out of `?1003`/`?1004` together. `TryReadInput` coalesces motion: while bytes are already waiting it folds a run of plain motion into the last report, and the first non-motion event is parked in the `_pending` slot for the next call (no added latency). Focus-out `ESC[O` becomes `ConsoleInputEvent.PointerLeft` (init property) and `InputEvent.PointerLeft`, because an SGR terminal sends no leave report; focus-in `ESC[I` is swallowed. The inspector's `hover` verb injects button-3 motion.
- **Caret = the real cursor.** `TextInputBar.Caret(...)` / `TextArea.Caret(...)` / `TextBar.Caret(column, ...)` opt into parking the terminal's actual cursor at the insertion point (`ITerminalViewport.SetCaret`, DECSCUSR shapes — `CaretStyle.BlinkingBar` is the thin editor bar) instead of painting a reverse-video cell; the terminal draws and blinks it. `VirtualTerminal` applies the request at the **end of `Flush`**, emits shape/visibility/position only on change (a Flush with no changed cells and the same caret writes nothing, because the cursor is known to still be parked; any sink write, immediate write, clear, resize or raw output clears that knowledge), and keeps the caret from darting during a paint by wrapping the frame in synchronized output (`?2026h`/`?2026l`, probed with DECRQM ahead of DA1 in `InitAsync`) rather than toggling DECTCEM, which restarts the blink; terminals without 2026 fall back to hide/show, only when there is a paint. The caret is also retracted during raw blits. Suppresses everything under `ColorMode.None`, and restores the user's configured shape on dispose (DECSCUSR is not alternate-screen state). The caret is sticky: whoever owns focus calls `HideCaret` when it should go away.
- **`ColorMode` enum** has a `None` value (ordinal 0) before `Sgr16` and `TrueColor`. Code that persisted or compared `ColorMode` by integer value may need updating. `ColorMode.None` suppresses all escape sequences for plain-text capture.
- **`ConsoleSize`** is how you ask for the terminal's width — not `System.Console.WindowWidth`, which on Windows throws `IOException` whenever stdout is redirected, because the CLR sizes via `GetStdHandle(STD_OUTPUT_HANDLE)` and that handle *is* the pipe. `TryGetWindowSize` / `GetWidth` try the managed properties first, then fall back (Windows only) to `WindowsConsoleInput.TryGetConsoleScreenBufferSize`, which opens `CONOUT$` — the attached console's screen buffer, reachable regardless of what stdout is wired to — and sizes from `srWindow` (inclusive edges, so `Right-Left+1`), never `dwSize`, whose height is the scrollback buffer. Read at call time, so live resizes track. mdcat, `examples/MarkdownConsole` and `VirtualTerminal`'s size seed + `Size` getter all resolve through it; the two hand-rolled `GetConsoleWidth()` copies that fell back to 80 are gone. **`_noConsole` deliberately stays `true` when stdout is redirected even though CONOUT$ can now measure the window** — `InitAsync`'s DA1 / cell-size probes write to stdout and read the reply back, so clearing it would inject escape sequences into the consumer's pipe and stall waiting for a reply that cannot come.
- **`MarkdownRenderer` math rendering** has three modes for display math (`$$...$$`, `\[...\]`) — Sixel (true raster), Unicode sextant (2×3 sub-cell blocks, no Sixel needed), and half-block (universal fallback). Inline math always renders as single-row Unicode. Callers pick the mode after probing terminal capability via DA1 (`HasSixelSupport`).
- **`MarkdownRenderer` image rendering** (`![alt](src)`) is opt-in via the `MarkdownImageOptions? images` parameter on `RenderLines` / `Render`. An image alone on its own line block-rasters via the **same** emit path as display math (`BoxRenderer.EncodeImage` — the encoder switch extracted so any RGBA buffer reuses it); an image mid-paragraph, or any image when `images` is null/unresolvable, renders as alt text (empty alt → file name). The `MdImage` node lives in **DIR.Lib.Markdown** (grammar `![` opener + `imageSpan` production mirroring `linkSpan`). Decoding uses **StbImageSharp** (PNG / baseline JPEG / BMP / GIF), already transitive via DIR.Lib → SharpAstro.Fonts — no new package, AOT-safe. The renderer never fetches: the host's `Resolver` maps a `src` to bytes (mdcat reads local files relative to the doc dir and returns null for `http(s)`/`data:` — **no network**).
