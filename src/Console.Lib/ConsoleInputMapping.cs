using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Maps <see cref="ConsoleKey"/> and <see cref="ConsoleModifiers"/> to the platform-agnostic
/// <see cref="InputKey"/> and <see cref="InputModifier"/> types from DIR.Lib.
/// </summary>
public static class ConsoleInputMapping
{
    extension(ConsoleKey key)
    {
        public InputKey ToInputKey => key switch
        {
            ConsoleKey.UpArrow => InputKey.Up,
            ConsoleKey.DownArrow => InputKey.Down,
            ConsoleKey.LeftArrow => InputKey.Left,
            ConsoleKey.RightArrow => InputKey.Right,
            ConsoleKey.Home => InputKey.Home,
            ConsoleKey.End => InputKey.End,
            ConsoleKey.PageUp => InputKey.PageUp,
            ConsoleKey.PageDown => InputKey.PageDown,
            ConsoleKey.Enter => InputKey.Enter,
            ConsoleKey.Escape => InputKey.Escape,
            ConsoleKey.Tab => InputKey.Tab,
            ConsoleKey.Spacebar => InputKey.Space,
            ConsoleKey.Backspace => InputKey.Backspace,
            ConsoleKey.Delete => InputKey.Delete,
            >= ConsoleKey.A and <= ConsoleKey.Z => (InputKey)((int)InputKey.A + (key - ConsoleKey.A)),
            >= ConsoleKey.D0 and <= ConsoleKey.D9 => (InputKey)((int)InputKey.D0 + (key - ConsoleKey.D0)),
            >= ConsoleKey.F1 and <= ConsoleKey.F12 => (InputKey)((int)InputKey.F1 + (key - ConsoleKey.F1)),
            ConsoleKey.OemPlus or ConsoleKey.Add => InputKey.Plus,
            ConsoleKey.OemMinus or ConsoleKey.Subtract => InputKey.Minus,
            _ => InputKey.None,
        };
    }

    extension(ConsoleModifiers modifiers)
    {
        public InputModifier ToInputModifier
        {
            get
            {
                var mod = InputModifier.None;
                if ((modifiers & ConsoleModifiers.Shift) != 0) mod |= InputModifier.Shift;
                if ((modifiers & ConsoleModifiers.Control) != 0) mod |= InputModifier.Ctrl;
                if ((modifiers & ConsoleModifiers.Alt) != 0) mod |= InputModifier.Alt;
                return mod;
            }
        }
    }

    extension(ConsoleInputEvent evt)
    {
        /// <summary>
        /// Converts the console input event's key and modifiers to platform-agnostic types.
        /// </summary>
        public (InputKey Key, InputModifier Modifiers) ToInput => (evt.Key.ToInputKey, evt.Modifiers.ToInputModifier);

        /// <summary>
        /// Converts the console input event to a platform-agnostic <see cref="InputEvent"/>.
        /// Mouse events take precedence; falls back to keyboard if no mouse data.
        /// </summary>
        public InputEvent? ToInputEvent
        {
            get
            {
                var modifiers = evt.Modifiers.ToInputModifier;

                if (evt.Mouse is { } mouse)
                {
                    // Scroll wheel: button 64 = up, 65 = down
                    if (mouse.Button is 64 or 65)
                    {
                        return new InputEvent.Scroll(
                            Delta: mouse.Button == 64 ? 1f : -1f,
                            X: mouse.X,
                            Y: mouse.Y,
                            Modifiers: modifiers);
                    }

                    var button = mouse.Button switch
                    {
                        0 => MouseButton.Left,
                        1 => MouseButton.Middle,
                        2 => MouseButton.Right,
                        _ => MouseButton.Left
                    };

                    return mouse.IsRelease
                        ? new InputEvent.MouseUp(mouse.X, mouse.Y, button)
                        : new InputEvent.MouseDown(mouse.X, mouse.Y, button, modifiers);
                }

                if (evt.Key is not ConsoleKey.None)
                {
                    var inputKey = evt.Key.ToInputKey;
                    if (inputKey != InputKey.None)
                    {
                        return new InputEvent.KeyDown(inputKey, modifiers);
                    }
                }

                return null;
            }
        }
    }
}
