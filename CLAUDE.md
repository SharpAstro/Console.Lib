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
