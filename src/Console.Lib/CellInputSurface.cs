using System;
using System.Collections.Generic;
using DIR.Lib;

namespace Console.Lib;

/// <summary>STUB: registers nothing (the behaviour before this leg). Replaced by the real surface.</summary>
public sealed class CellInputSurface(WindowUiSettings ui) : IPixelWidget
{
    public WindowUiSettings Ui { get; } = ui;

    public ListCursor ListCursor { get; } = new();

    public (float X, float Y)? Pointer { get; set; }

    public bool HandleInput(InputEvent evt) => false;

    public HitResult? HitTest(float x, float y) => null;

    public List<TextInputState> GetRegisteredTextInputs() => [];

    public int CaretIndexAt(HitResult.TextInputHit hit, float pointerX) => 0;

    public void CollectPaintedRegions(List<ClickableRegion> into) { }

    public void CollectPaintedNodes(List<Layout.ArrangedNode<float>> into) { }

    public ListScrollController? ScrollTargetAt(float x, float y) => null;

    public CursorKind? HitTestCursor(float x, float y) => null;

    public bool HandleListKey(InputKey key, InputModifier modifier = InputModifier.None) => false;
}

/// <summary>STUB entry point.</summary>
public static class CellInputRouter
{
    public static InputRouter Create(CellInputSurface surface, BackgroundTaskTracker tracker, Action requestRedraw,
        Func<InputEvent, bool>? fallback = null)
        => new(surface.Ui, tracker, requestRedraw)
        {
            Widgets = () => [surface],
            Unhandled = evt => fallback?.Invoke(evt) ?? false,
        };
}
