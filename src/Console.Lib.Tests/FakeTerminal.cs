using Console.Lib;

namespace Console.Lib.Tests;

internal sealed class FakeTerminal : IVirtualTerminal
{
    private readonly Queue<ConsoleInputEvent> _inputs;
    private int _width, _height;

    public FakeTerminal(Queue<ConsoleInputEvent> inputs, int width = 80, int height = 24)
    {
        _inputs = inputs;
        _width = width;
        _height = height;
    }

    public bool IsAlternateScreen { get; private set; }
    public (int Column, int Row) Offset => (0, 0);
    public (int Width, int Height) Size => (_width, _height);
    public (int Left, int Top)? LastCursorPosition { get; private set; }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public void Clear() { }
    public void SetCursorPosition(int left, int top) => LastCursorPosition = (left, top);
    public void Write(string text) { }
    public void WriteLine(string? text = null) { }
    public void Flush() => FlushCount++;

    /// <summary>How many times <see cref="Flush"/> was called — a flush mid-paint ships a half-painted
    /// diff, so tests assert on WHEN flushes happen, not just that output eventually goes out.</summary>
    public int FlushCount { get; private set; }
    public Stream OutputStream { get; } = Stream.Null;
    public bool HasInput() => _inputs.Count > 0;
    public ConsoleInputEvent TryReadInput() => _inputs.Dequeue();
    public Task InitAsync() => Task.CompletedTask;
    public ImageDisplayCapability ImageDisplayCapability => ImageDisplayCapability.NoColor;
    public bool HasSixelSupport => false;
    public bool HasColorSupport => false;
    public bool IsInputRedirected => false;
    public bool IsOutputRedirected => false;
    public TermCell CellSize => new(10, 20);

    public void EnterAlternateScreen()
    {
        IsAlternateScreen = true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
