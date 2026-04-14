# Console.Lib

A .NET library for building terminal applications with dock-based layouts, widgets, mouse/keyboard input, VT styling, and Sixel graphics rendering. AOT-compatible, targeting .NET 10.

[![CI/CD](https://github.com/SharpAstro/Console.Lib/actions/workflows/dotnet.yml/badge.svg)](https://github.com/SharpAstro/Console.Lib/actions/workflows/dotnet.yml)

## Features

- **Dock-based layout** — top/bottom/left/right/fill panels that recompute on terminal resize
- **Widget system** — `TextBar`, `ScrollableList<T>`, `Canvas<TSurface>`, and `MarkdownWidget`
- **Sixel graphics** — high-performance encoder (14x faster than ImageMagick's built-in Sixel writer)
- **VT styling** — `VtStyle` with 16-color SGR and TrueColor modes, automatic capability detection
- **Markdown rendering** — headings, tables, lists, inline `[text]{color}` syntax via Markdig
- **Input handling** — unified keyboard + mouse events, SGR mouse tracking in alternate screen
- **Menu system** — arrow-key navigation, digit shortcuts, mouse support, alternate/normal mode
- **Cross-platform** — Windows VT I/O via Win32, native VT100 on Unix/macOS

## Quick start

```bash
dotnet add package Console.Lib
```

```csharp
var terminal = new VirtualTerminal();
await terminal.InitAsync();

terminal.EnterAlternateScreen();

var panel = new Panel(terminal);
var statusBar = new TextBar(panel.Dock(DockStyle.Bottom, 1));
var main = panel.Fill();

statusBar.Text(" Ready").Style(new VtStyle(SgrColor.BrightWhite, SgrColor.BrightBlack)).Render();
```

## Building

```bash
dotnet build src/Console.Lib
dotnet test src/Console.Lib.Tests
```

## Documentation

See [`src/Console.Lib/README.md`](src/Console.Lib/README.md) for the full API reference, architecture diagram, and detailed usage guide.

## License

[MIT](LICENSE)
