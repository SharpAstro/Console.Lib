using Console.Lib;
using DIR.Lib;
using Xunit;
namespace Console.Lib.Tests;
public class TmpDbgTests
{
    [Fact]
    public void Dump()
    {
        var m = new MenuModel(); m.Reset("Title", "Pick:", ["Alpha","Beta","Gamma"]);
        var s = new MenuPanelStyle { MinWidth = 24f, MaxWidth = 60f, SideMargin = 2f, FontToCard = 1f/56f, Scrim = new RGBAColor32(0,0,0,0) };
        var tree = MenuLayout.BuildPanel(m, new MenuColors(), s, CellFloatMeasureContext.Instance, 60, 24);
        var arr = Layout.Engine.Arrange(tree, new Rect<int>(0,0,60,24), CellMeasureContext.CellAuthored);
        var o = string.Join("\n", arr.Select((a,i) => $"{i} d{a.Depth} {a.Node.GetType().Name} {a.Bounds} hit={a.Node.Hit?.GetType().Name} click={a.Node.OnClick is not null}"));
        System.IO.File.WriteAllText(@"C:\Users\SebastianGodelet\.claude\jobs\74229581\tmp\dump.txt", o);
        Assert.Fail(o);
    }
}
