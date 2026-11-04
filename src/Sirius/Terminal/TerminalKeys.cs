using Avalonia.Input;

namespace Sirius.Terminal;

// Traduz as teclas para o que os programas de terminal esperam receber.
public static class TerminalKeys
{
    public static string? Encode(Key key, KeyModifiers modifiers, bool applicationCursorKeys)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var control = modifiers.HasFlag(KeyModifiers.Control);
        var parameter = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);

        switch (key)
        {
            case Key.Enter:
                return "\r";
            case Key.Back:
                return control ? "\x08" : alt ? "\x1b\x7f" : "\x7f";
            case Key.Tab:
                return shift ? "\x1b[Z" : "\t";
            case Key.Escape:
                return "\x1b";
            case Key.Up:
                return Arrow('A', parameter, applicationCursorKeys);
            case Key.Down:
                return Arrow('B', parameter, applicationCursorKeys);
            case Key.Right:
                return Arrow('C', parameter, applicationCursorKeys);
            case Key.Left:
                return Arrow('D', parameter, applicationCursorKeys);
            case Key.Home:
                return Arrow('H', parameter, applicationCursorKeys);
            case Key.End:
                return Arrow('F', parameter, applicationCursorKeys);
            case Key.Insert:
                return Tilde(2, parameter);
            case Key.Delete:
                return Tilde(3, parameter);
            case Key.PageUp:
                return Tilde(5, parameter);
            case Key.PageDown:
                return Tilde(6, parameter);
            case >= Key.F1 and <= Key.F4:
                return parameter == 1 ? "\x1bO" + (char)('P' + (key - Key.F1)) : $"\x1b[1;{parameter}{(char)('P' + (key - Key.F1))}";
            case >= Key.F5 and <= Key.F12:
                return Tilde(FunctionCodes[key - Key.F5], parameter);
        }

        // Ctrl e Alt juntos são o AltGr dos teclados brasileiros: o texto vem pelo evento de digitação.
        if (control && alt)
            return null;

        if (control)
        {
            if (key is >= Key.A and <= Key.Z)
                return ((char)(key - Key.A + 1)).ToString();
            if (key == Key.Space)
                return "\0";
            return null;
        }

        if (alt && key is >= Key.A and <= Key.Z)
        {
            var letter = (char)((shift ? 'A' : 'a') + (key - Key.A));
            return "\x1b" + letter;
        }

        return null;
    }

    private static readonly int[] FunctionCodes = [15, 17, 18, 19, 20, 21, 23, 24];

    private static string Arrow(char final, int parameter, bool applicationCursorKeys)
    {
        if (parameter != 1)
            return $"\x1b[1;{parameter}{final}";
        return (applicationCursorKeys ? "\x1bO" : "\x1b[") + final;
    }

    private static string Tilde(int code, int parameter) =>
        parameter == 1 ? $"\x1b[{code}~" : $"\x1b[{code};{parameter}~";
}
