---
name: console-lib
description: Bootstrap context for the Console.Lib repo. Run at the start of a session to recover the canonical state — what's in the library, how the dock + widget system fits together, the version policy, where DIR.Lib sits in the dependency chain, and the verify-before-push checklist. Invoke when you need "what is this repo and what should I do next?".
---

# Console.Lib repo skill

A .NET 10 / AOT-compatible terminal-app library: dock layout, widgets (`TextBar`, `TextInputBar`, `TextArea`, `ScrollableList`, `TreeView`, `Canvas`, `MarkdownWidget`), VT styling, Sixel graphics, mouse + keyboard input. Architecture and conventions live in `CLAUDE.md` at the repo root — read it first if you haven't. This skill is the *what's done / what's next / how to verify* layer on top.

## Read these first, in order

1. `CLAUDE.md` — versioning policy, key design notes, AOT gotchas. Authoritative.
2. `src/Console.Lib/README.md` — full API reference + architecture diagram.
3. `git log --oneline -10` — recent commits.

## Repo layout

| Path | Purpose |
|---|---|
| `src/Console.Lib/` | The library. `IsAotCompatible=true`. Targets `net10.0`. Builds a NuGet package on `dotnet build` (`GeneratePackageOnBuild=true`). |
| `src/Console.Lib.Tests/` | xUnit tests. `FakeTerminal.cs` is the in-memory `IVirtualTerminal` that the tests use to drive widgets without a real TTY. |
| `nupkg/` and `nupkgs/` | Local package output. Not pushed to git. |
| `out/` | Other build artifacts. Not pushed to git. |

The library has a sibling **DIR.Lib** dependency (rendering primitives, `RGBAColor32`, `DockStyle`). The csproj uses a conditional reference: if `../../../DIR.Lib/src/DIR.Lib/DIR.Lib.csproj` exists, MSBuild picks it up as a `ProjectReference` (in-tree iteration); otherwise falls back to the NuGet `PackageReference`. Toggle explicitly via `-p:UseLocalDirLib=true|false`.

## Public API map

- **Terminal lifecycle**: `IVirtualTerminal` / `VirtualTerminal` — `InitAsync`, `EnterAlternateScreen`, `TryReadInput`, `HasInput`, `Clear`. On Windows, alternate-screen entry flips `WindowsConsoleInput.EnableVirtualTerminalIO` and SGR mouse mode.
- **Viewports**: `ITerminalViewport`, `TerminalViewport` (sub-region with translated coordinates).
- **Layout**: `DockStyle` (in `DIR.Lib`), `TerminalLayout`, `Panel` (collection of widgets sharing a layout).
- **Widgets**: `Widget` (abstract base; `Render`, `HitTest`), `TextBar`, `TextInputBar` (single-line editor, uses `TextInputState`), `TextArea` (multi-line editor on a UTF-8 `GapBuffer`; `TextAreaState` exposes `MemoryBeforeGap`/`MemoryAfterGap` for zero-alloc pipe consumers), `ScrollableList<T>` (cursor + selection + optional multi-column mode via `Columns(n)` / `ColumnIndex` / `MoveColumn` and the column-aware `IRowFormatter.FormatRow(int, ColorMode, bool, int, int)` overload), `TreeView<T>` (cursor + expand/collapse), `Canvas<TSurface>`, `MarkdownWidget`.
- **Input**: `ConsoleInputEvent` (mouse + key + `KeyChar` — a `Rune?` carrying the decoded UTF-8 codepoint when the byte stream produced one; null for navigation/control keys and mouse events), `MouseEvent` (with SGR coordinates), `ConsoleInputMapping`. `VirtualTerminal` populates `KeyChar` from the raw stdin byte stream including UTF-8 continuation-byte buffering, so non-ASCII input (`é`, `中`, emoji) round-trips into widgets without going through the US-layout `InputKeyCharMapping` table. `TextArea.HandleChar(ConsoleInputEvent)` and `TextInputBar.HandleInput(ConsoleInputEvent)` consume the event directly — these replaced the old `(ConsoleKey, ConsoleModifiers)` / `(InputKey, InputModifier)` overloads in 2.8 (callers must pass the event).
- **Styling**: `VtStyle`, `SgrColor`, `ColorMode` (`None` / `Sgr16` / `TrueColor`).
- **Sixel**: `SixelEncoder` (high-perf, ArrayPool-backed), `SixelRenderer<TSurface>`, `Canvas<T>.EncodeSixel`.
- **Menus**: `MenuBase<T>` — fullscreen menu with arrow + digit + mouse navigation.
- **Markdown**: `MarkdownRenderer`, `MarkdownTheme`. Inline `[text]{color}` syntax, headings, tables, lists.

## Versioning

Per `CLAUDE.md`: SemVer `Major.Minor.Patch`. Three places must stay in sync when bumping the major or minor:

| File | Property | Format |
|---|---|---|
| `src/Console.Lib/Console.Lib.csproj` | `<VersionPrefix>` | `X.Y.Z` |
| `src/Console.Lib/Console.Lib.csproj` | `<AssemblyVersion>` | `X.Y.0.0` (only changes on binary breaks) |
| `.github/workflows/dotnet.yml` | `VERSION_PREFIX` | `X.Y.${{ github.run_number }}` |

Adding new public methods or default-implemented interface members ⇒ minor bump. Removing/renaming public members or changing signatures ⇒ major bump (and `AssemblyVersion` bump). Pure bug fixes ⇒ patch bump (csproj `VersionPrefix` only).

CI publishes to NuGet on push to `main`. **Consumers waiting on a new feature must wait for the workflow to finish** — there is no manual publish path from a developer machine.

## Canonical re-verification (run before any commit)

```bash
dotnet build src/Console.Lib -c Debug --nologo                 # 0 warnings, generates nupkg
dotnet test  src/Console.Lib.Tests --nologo                    # all green (currently 114)
dotnet pack  src/Console.Lib -c Release -o ../nupkgs           # nupkg + snupkg
```

The build itself produces a `.nupkg` because `GeneratePackageOnBuild=true`. Local consumers can point a `<PackageReference>` at the nupkg via a project-local `nuget.config` source, but the standard pattern is to wait for the CI publish.

## Conventions and constraints

- **AOT-compatible** (`IsAotCompatible=true`). No reflection in widget code; styling/layout is allocation-light. New code must follow.
- **Records and immutable structs** for value-type API surfaces (`VtStyle`, `MouseEvent`, `ConsoleInputEvent`, `RGBAColor32` from DIR.Lib). Match the style.
- **Render is allocation-aware**. The widgets compose styled rows in `StringBuilder` and emit a single `Viewport.Write` per row — multiple writes per row visibly slow Windows console hosts. Don't break that.
- **Mouse events use pixel coordinates**; widgets convert to cell-grid via `TermCell.Width` / `TermCell.Height`. `Widget.HitTest(pixelX, pixelY)` returns viewport-local cell coordinates or `null`.
- **Default-implemented interface members** are the migration tool of choice when extending `IRowFormatter` / `ITreeNode<T>` (added in C# 8). Mirror what's there: existing implementers stay binary-compatible.
- **`FakeTerminal` is the test-side `IVirtualTerminal`**. Drive widgets through a `TerminalViewport` over `FakeTerminal` to test sizing, scrolling, hit-testing, etc. — no real TTY required.

## Common consumer integration patterns

A consumer who depends on Console.Lib via NuGet typically:

1. `dotnet add package Console.Lib`
2. (If they also want DIR.Lib's types like `DockStyle` directly) `dotnet add package DIR.Lib` — Console.Lib re-exports the namespace but consumers must reference it for `using DIR.Lib;` to compile.
3. `using CL = global::Console.Lib;` if their codebase has a `Console` namespace collision (the library's namespace shadows `System.Console`).

The `CL` alias pattern shows up in every consumer (filetreewalker, the LALR TUI, etc.).

## When stuck

- `git log --oneline -20` — what landed recently
- `src/Console.Lib/README.md` — API + diagrams
- `src/Console.Lib.Tests/FakeTerminal.cs` — how to drive widgets in tests
- `src/Console.Lib.Tests/ScrollableListTests.cs` / `MenuTests.cs` — concrete examples of widget testing
