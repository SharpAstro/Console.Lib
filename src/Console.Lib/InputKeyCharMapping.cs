using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Maps <see cref="InputKey"/> values to printable characters.
/// Handles shift for uppercase letters and shifted symbols.
/// </summary>
public static class InputKeyCharMapping
{
    extension(InputKey key)
    {
        /// <summary>
        /// Returns the printable character for this key, or <c>null</c> if the key
        /// does not produce a printable character.
        /// </summary>
        public char? ToChar(InputModifier modifiers = InputModifier.None)
        {
            var shift = (modifiers & InputModifier.Shift) != 0;

            return key switch
            {
                >= InputKey.A and <= InputKey.Z => shift
                    ? (char)('A' + (key - InputKey.A))
                    : (char)('a' + (key - InputKey.A)),

                >= InputKey.D0 and <= InputKey.D9 when !shift => (char)('0' + (key - InputKey.D0)),

                // Shifted digits (US layout)
                InputKey.D1 when shift => '!',
                InputKey.D2 when shift => '@',
                InputKey.D3 when shift => '#',
                InputKey.D4 when shift => '$',
                InputKey.D5 when shift => '%',
                InputKey.D6 when shift => '^',
                InputKey.D7 when shift => '&',
                InputKey.D8 when shift => '*',
                InputKey.D9 when shift => '(',
                InputKey.D0 when shift => ')',

                InputKey.Space => ' ',
                InputKey.Minus => shift ? '_' : '-',
                InputKey.Plus => shift ? '+' : '=',
                InputKey.Period => shift ? '>' : '.',
                InputKey.Comma => shift ? '<' : ',',
                InputKey.Slash => shift ? '?' : '/',
                InputKey.Backslash => shift ? '|' : '\\',
                InputKey.Semicolon => shift ? ':' : ';',
                InputKey.Quote => shift ? '"' : '\'',
                InputKey.BracketLeft => shift ? '{' : '[',
                InputKey.BracketRight => shift ? '}' : ']',
                InputKey.Grave => shift ? '~' : '`',

                _ => null
            };
        }
    }
}
