# CLAUDE.md

## Build & Test

```bash
dotnet build src/Console.Lib
dotnet test src/Console.Lib.Tests
```

## Versioning

The version is defined in two places — both must be updated together:

1. **`src/Console.Lib/Console.Lib.csproj`** — `<AssemblyVersion>` (format: `X.Y.0.0`)
2. **`.github/workflows/dotnet.yml`** — `VERSION_PREFIX` env var (format: `X.Y.${{ github.run_number }}`)

The CI workflow composes the full package version from `VERSION_PREFIX`, `VERSION_REV`, and `VERSION_HASH`.

When bumping the version, update both files to keep them in sync.

## Key design notes

- **Windows VT I/O** (`WindowsConsoleInput.EnableVirtualTerminalIO`) is only activated when entering alternate screen mode, not during `InitAsync()`. This keeps `Console.ReadKey` working correctly in normal (non-alternate) mode for ASCII/text-based UIs.
- **`TryReadInput`** uses `intercept: true` in normal mode — keystrokes are never echoed. Callers control display feedback (e.g., via `WriteInPlace`).
- **`MenuBase<T>`** in normal mode shows a `> ` prompt and echoes the selected item on confirmation.
- **`ColorMode` enum** now has a `None` value (ordinal 0) before `Sgr16` and `TrueColor`. Code that persisted or compared `ColorMode` by integer value may need updating.
