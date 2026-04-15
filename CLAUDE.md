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

## Key design notes

- **Windows VT I/O** (`WindowsConsoleInput.EnableVirtualTerminalIO`) is only activated when entering alternate screen mode, not during `InitAsync()`. This keeps `Console.ReadKey` working correctly in normal (non-alternate) mode for ASCII/text-based UIs.
- **`TryReadInput`** uses `intercept: true` in normal mode — keystrokes are never echoed. Callers control display feedback (e.g., via `WriteInPlace`).
- **`MenuBase<T>`** in normal mode shows a `> ` prompt and echoes the selected item on confirmation.
- **`ColorMode` enum** now has a `None` value (ordinal 0) before `Sgr16` and `TrueColor`. Code that persisted or compared `ColorMode` by integer value may need updating.
