using DIR.Lib;
using Markdig.Syntax.Inlines;

namespace Console.Lib;

/// <summary>
/// A Markdig inline AST node representing colored text: <c>[text]{color}</c>.
/// The color is resolved at render time to respect the active <see cref="ColorMode"/>.
/// </summary>
public class ColorInline : ContainerInline
{
    public RGBAColor32 Color { get; set; }
}
