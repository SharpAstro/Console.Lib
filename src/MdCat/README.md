# mdcat

A tiny `cat` for Markdown. Reads a Markdown file (or stdin) and renders it to
styled terminal output using [`Console.Lib`](../Console.Lib)'s `MarkdownRenderer`
— headings, bold/italic, inline code, lists, blockquotes, fenced code blocks,
OSC 8 hyperlinks, and LaTeX math.

## Usage

```
mdcat [options] [file]
```

| Argument | Meaning |
|---|---|
| `file` | Markdown file to render. Use `-` for stdin. Omit to read redirected stdin, else show help. |

| Option | Meaning |
|---|---|
| `-h`, `--help` | Show help. |
| `--mode <encoding>` | Display-math rendering: `unicode` \| `sixel` \| `sextant` \| `halfblock`. Default: auto-detect via DA1 (sixel on capable terminals, sextant otherwise). |
| `--color <mode>` | `truecolor` \| `16` \| `none`. Default: auto-detect. |
| `--no-color`, `--plain` | Plain text, no escape sequences. Shorthand for `--color none`. |
| `--width <N>` | Render width in columns. Default: console width, or 80. |

**Color auto-detection.** With no `--color`, mdcat emits truecolor and, when it
detects a 24-bit-capable terminal (`COLORTERM=truecolor`/`24bit`, or a known
terminal via `WT_SESSION` / `TERM_PROGRAM`), switches to a richer GitHub-Dark
palette; otherwise it uses the default 16-color-safe palette. An explicit
`--color` overrides detection. `NO_COLOR` (see
[no-color.org](https://no-color.org/)) disables color unless `--color` overrides it.

### Examples

```bash
mdcat README.md                  # render a file
cat README.md | mdcat -          # render from stdin
mdcat --mode unicode notes.md    # force Unicode math (no raster)
mdcat --plain README.md > out.txt # strip styling for plain capture
```

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (also `--help`). |
| `1` | I/O error — file not found or unreadable input. |
| `2` | Bad arguments. |

## Math

Display math rasterizes (sixel / sextant / half-block per `--mode`) only when
the delimiters sit on their **own lines** — i.e. a block:

```
$$
\int_0^1 x^2\,dx = \frac{1}{3}
$$
```

Single-line `$$...$$` and inline `$...$` always render as single-row Unicode,
never rastered.

Rastering needs an OpenType math font. mdcat **bundles** STIX Two Math
(`Fonts/STIX2Math.otf`, SIL OFL — see `Fonts/STIX2-OFL.txt`) next to the
executable, so math renders the same everywhere without relying on system
fonts. If that file is ever missing, `MarkdownRenderer` falls back to an
internal system-font search (STIX Two Math, Cambria Math, …) and, failing
that, a Unicode-only approximation.

## Install

### As a .NET global tool (needs the .NET 10 runtime)

```bash
dotnet tool install --global mdcat
```

Auto-updatable (`dotnet tool update -g mdcat`) and discoverable via
`dotnet tool search mdcat`. The bundled math font ships inside the package, so
display math works out of the box.

### Prebuilt native binary (no runtime required)

Download a self-contained binary from the
[Releases](https://github.com/SharpAstro/Console.Lib/releases) page. Native AOT
builds are published for:

| OS | x64 | arm64 |
|---|---|---|
| Linux | `linux-x64` | `linux-arm64` |
| Windows | `win-x64` | `win-arm64` |
| macOS | `osx-x64` | `osx-arm64` |

Each archive contains the `mdcat` executable plus the `Fonts/` directory (the
bundled math font must sit next to the binary). Extract it and put the folder
on your `PATH`, e.g.:

```bash
tar -xzf mdcat-1.0.0-linux-x64.tar.gz
sudo cp -r mdcat-1.0.0-linux-x64/* /usr/local/bin/   # mdcat + Fonts/
```

Releases are cut by pushing a `mdcat-vX.Y.Z` tag, which runs
[`.github/workflows/mdcat-release.yml`](../../.github/workflows/mdcat-release.yml).

## Build from source

```bash
dotnet build src/MdCat                          # build
dotnet run --project src/MdCat -- README.md     # run in place

# native, self-contained binary for the current platform
dotnet publish src/MdCat -c Release -r <rid> --self-contained -o out
# (rid = linux-x64 | win-x64 | osx-arm64 | …; output in out/, with out/Fonts/)

# or install as a .NET global tool from a local package
dotnet pack src/MdCat -c Release
dotnet tool install --global --add-source src/MdCat/bin/Release mdcat
```
