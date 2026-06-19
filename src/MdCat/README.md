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
| `--color <mode>` | `truecolor` \| `16` \| `none`. Default: `truecolor`, or `none` when `NO_COLOR` is set. |
| `--no-color`, `--plain` | Plain text, no escape sequences. Shorthand for `--color none`. |
| `--width <N>` | Render width in columns. Default: console width, or 80. |

`NO_COLOR` (see [no-color.org](https://no-color.org/)) is honored: any non-empty
value disables color unless an explicit `--color` overrides it.

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

## Build & install

```bash
dotnet build src/MdCat               # build
dotnet run --project src/MdCat -- README.md   # run in place

# install as a global tool from a local package
dotnet pack src/MdCat -c Release
dotnet tool install --global --add-source src/MdCat/bin/Release mdcat
```
