using System.Text;

namespace Sirius.Terminal;

[Flags]
public enum CellFlags { None = 0, Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16 }

// Cores: -1 é a padrão, 0 a 255 são da tabela do terminal, e TrueColorBit marca RGB.
public readonly record struct CellStyle(int Foreground, int Background, CellFlags Flags)
{
    public const int TrueColorBit = 0x1000000;
    public static readonly CellStyle Default = new(-1, -1, CellFlags.None);
}

// Rune 0 é uma célula vazia, e -1 é a metade direita de um caractere largo.
public readonly record struct Cell(int Rune, CellStyle Style);

public sealed class TerminalLine(int columns, CellStyle style)
{
    public Cell[] Cells { get; set; } = Blank(columns, style);
    public bool Wrapped { get; set; }

    public static Cell[] Blank(int columns, CellStyle style)
    {
        var cells = new Cell[columns];
        Array.Fill(cells, new Cell(0, style));
        return cells;
    }
}

// Interpreta as sequências que o ConPTY envia e mantém a tela e o histórico.
public sealed class TerminalBuffer
{
    public const int MaxScrollback = 5000;
    private const int ScrollbackTrimStep = 500;

    private enum State { Ground, Escape, EscapeSkip, Csi, String, StringEscape }

    private readonly List<TerminalLine> _scrollback = new();
    private readonly StringBuilder _csi = new();
    private TerminalLine[] _main;
    private TerminalLine[]? _alternate;
    private TerminalLine[] _screen;
    private State _state;
    private char _highSurrogate;
    private CellStyle _style = CellStyle.Default;
    private int _scrollTop;
    private int _scrollBottom;
    private bool _pendingWrap;
    private bool _autoWrap = true;
    private int _savedRow;
    private int _savedColumn;
    private CellStyle _savedStyle = CellStyle.Default;

    public TerminalBuffer(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        _main = NewScreen(columns, rows);
        _screen = _main;
        _scrollBottom = rows - 1;
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; private set; }
    public int CursorColumn { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }

    // O programa quer receber o mouse, como o Claude Code para rolar a conversa.
    public bool MouseTracking { get; private set; }
    public bool SgrMouse { get; private set; }
    public bool IsAlternateScreen => _alternate is not null;
    public int ScrollbackCount => IsAlternateScreen ? 0 : _scrollback.Count;
    public int TotalLines => ScrollbackCount + Rows;
    public int CursorLine => ScrollbackCount + CursorRow;

    // O que o programa pediu para o terminal responder.
    public event Action<string>? Reply;

    public TerminalLine GetLine(int index) =>
        index < ScrollbackCount ? _scrollback[index] : _screen[index - ScrollbackCount];

    public void Feed(string text)
    {
        foreach (var c in text)
            Step(c);
    }

    public void Resize(int columns, int rows)
    {
        if (columns == Columns && rows == Rows)
            return;

        ResizeScreen(ref _main, columns, rows, !IsAlternateScreen, CursorRow, out var newRow);
        if (_alternate is not null)
        {
            var alternate = _alternate;
            ResizeScreen(ref alternate, columns, rows, false, CursorRow, out newRow);
            _alternate = alternate;
            _screen = alternate;
        }
        else
        {
            _screen = _main;
        }

        Columns = columns;
        Rows = rows;
        CursorRow = newRow;
        CursorColumn = Math.Min(CursorColumn, columns - 1);
        _pendingWrap = false;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
    }

    // O texto de uma linha lógica, juntando as partes quebradas pela largura da tela.
    // Cada célula vira exatamente um caractere, para as posições continuarem batendo.
    public (string Text, int FirstLine) LogicalLine(int index)
    {
        var first = index;
        while (first > 0 && GetLine(first - 1).Wrapped)
            first--;

        var builder = new StringBuilder();
        var current = first;
        while (true)
        {
            var line = GetLine(current);
            foreach (var cell in line.Cells)
                builder.Append(CellChar(cell));
            if (!line.Wrapped || current + 1 >= TotalLines)
                break;
            current++;
        }
        return (builder.ToString().TrimEnd(), first);
    }

    public string LineText(int index)
    {
        var builder = new StringBuilder();
        foreach (var cell in GetLine(index).Cells)
        {
            if (cell.Rune != -1)
                builder.Append(cell.Rune > 0 ? char.ConvertFromUtf32(cell.Rune) : " ");
        }
        return builder.ToString().TrimEnd();
    }

    public string AllText()
    {
        var lines = new List<string>();
        for (var i = 0; i < TotalLines; i++)
            lines.Add(LineText(i));
        return string.Join('\n', lines).TrimEnd();
    }

    private static char CellChar(Cell cell) =>
        cell.Rune is > 0 and <= 0xFFFF ? (char)cell.Rune : cell.Rune == 0 || cell.Rune == -1 ? ' ' : '?';

    private void Step(char c)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(c);
                break;
            case State.Escape:
                Escape(c);
                break;
            case State.EscapeSkip:
                _state = State.Ground;
                break;
            case State.Csi:
                if (c is >= '@' and <= '~')
                {
                    _state = State.Ground;
                    Dispatch(c);
                }
                else if (c == '\x1b')
                {
                    _state = State.Escape;
                }
                else
                {
                    _csi.Append(c);
                }
                break;
            case State.String:
                if (c == '\a')
                    _state = State.Ground;
                else if (c == '\x1b')
                    _state = State.StringEscape;
                break;
            case State.StringEscape:
                _state = c == '\\' ? State.Ground : State.String;
                break;
        }
    }

    private void Ground(char c)
    {
        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return;
        }
        if (char.IsLowSurrogate(c))
        {
            if (_highSurrogate != 0)
                Print(char.ConvertToUtf32(_highSurrogate, c));
            _highSurrogate = '\0';
            return;
        }

        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                break;
            case '\r':
                CursorColumn = 0;
                _pendingWrap = false;
                break;
            case '\n' or '\v' or '\f':
                LineFeed();
                break;
            case '\b':
                CursorColumn = Math.Max(0, CursorColumn - 1);
                _pendingWrap = false;
                break;
            case '\t':
                CursorColumn = Math.Min(Columns - 1, (CursorColumn / 8 + 1) * 8);
                _pendingWrap = false;
                break;
            default:
                if (c >= ' ' && c != '\x7f')
                    Print(c);
                break;
        }
    }

    private void Escape(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _csi.Clear();
                _state = State.Csi;
                break;
            case ']' or 'P' or '^' or '_' or 'X':
                _state = State.String;
                break;
            case '(' or ')' or '*' or '+' or '#' or '%':
                _state = State.EscapeSkip;
                break;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CursorColumn = 0;
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                Reset();
                break;
        }
    }

    private void Print(int rune)
    {
        var width = RuneWidth(rune);
        if (width == 0)
            return;

        if (_autoWrap && (_pendingWrap || CursorColumn + width > Columns))
        {
            _screen[CursorRow].Wrapped = true;
            CursorColumn = 0;
            LineFeed();
        }
        _pendingWrap = false;

        var cells = _screen[CursorRow].Cells;
        var column = Math.Min(CursorColumn, Columns - width);
        cells[column] = new Cell(rune, _style);
        if (width == 2)
            cells[column + 1] = new Cell(-1, _style);

        CursorColumn = column + width;
        if (CursorColumn >= Columns)
        {
            CursorColumn = Columns - 1;
            _pendingWrap = _autoWrap;
        }
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        if (CursorRow == _scrollBottom)
            ScrollUp(1);
        else if (CursorRow < Rows - 1)
            CursorRow++;
    }

    private void ReverseIndex()
    {
        _pendingWrap = false;
        if (CursorRow == _scrollTop)
            ScrollDown(1);
        else if (CursorRow > 0)
            CursorRow--;
    }

    private void ScrollUp(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        for (var n = 0; n < count; n++)
        {
            var removed = _screen[_scrollTop];
            if (_scrollTop == 0 && !IsAlternateScreen)
                AddToScrollback(removed);
            Array.Copy(_screen, _scrollTop + 1, _screen, _scrollTop, _scrollBottom - _scrollTop);
            _screen[_scrollBottom] = new TerminalLine(Columns, BlankStyle());
        }
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        for (var n = 0; n < count; n++)
        {
            Array.Copy(_screen, _scrollTop, _screen, _scrollTop + 1, _scrollBottom - _scrollTop);
            _screen[_scrollTop] = new TerminalLine(Columns, BlankStyle());
        }
    }

    private void AddToScrollback(TerminalLine line)
    {
        _scrollback.Add(line);
        if (_scrollback.Count >= MaxScrollback + ScrollbackTrimStep)
            _scrollback.RemoveRange(0, ScrollbackTrimStep);
    }

    private CellStyle BlankStyle() => new(-1, _style.Background, CellFlags.None);

    private void Dispatch(char final)
    {
        var body = _csi.ToString();
        var isPrivate = body.StartsWith('?');
        if (isPrivate)
            body = body[1..];
        else if (body.Length > 0 && body[0] is '>' or '=' or '<')
            return;
        if (body.Any(ch => ch is >= ' ' and <= '/'))
            return;

        var args = body.Length == 0 ? [] : body.Split(';', ':');
        int Arg(int index, int fallback) =>
            index < args.Length && int.TryParse(args[index], out var value) && value > 0 ? value : fallback;
        int Raw(int index) =>
            index < args.Length && int.TryParse(args[index], out var value) ? value : 0;

        if (isPrivate)
        {
            if (final is 'h' or 'l')
                SetPrivateMode(args, final == 'h');
            return;
        }

        switch (final)
        {
            case 'A':
                MoveCursor(CursorRow - Arg(0, 1), CursorColumn, clampToRegion: true);
                break;
            case 'B' or 'e':
                MoveCursor(CursorRow + Arg(0, 1), CursorColumn, clampToRegion: true);
                break;
            case 'C' or 'a':
                MoveCursor(CursorRow, CursorColumn + Arg(0, 1));
                break;
            case 'D':
                MoveCursor(CursorRow, CursorColumn - Arg(0, 1));
                break;
            case 'E':
                MoveCursor(CursorRow + Arg(0, 1), 0, clampToRegion: true);
                break;
            case 'F':
                MoveCursor(CursorRow - Arg(0, 1), 0, clampToRegion: true);
                break;
            case 'G' or '`':
                MoveCursor(CursorRow, Arg(0, 1) - 1);
                break;
            case 'd':
                MoveCursor(Arg(0, 1) - 1, CursorColumn);
                break;
            case 'H' or 'f':
                MoveCursor(Arg(0, 1) - 1, Arg(1, 1) - 1);
                break;
            case 'J':
                EraseDisplay(Raw(0));
                break;
            case 'K':
                EraseLine(Raw(0));
                break;
            case 'L':
                InsertLines(Arg(0, 1));
                break;
            case 'M':
                DeleteLines(Arg(0, 1));
                break;
            case '@':
                InsertCells(Arg(0, 1));
                break;
            case 'P':
                DeleteCells(Arg(0, 1));
                break;
            case 'X':
                EraseCells(Arg(0, 1));
                break;
            case 'S':
                ScrollUp(Arg(0, 1));
                break;
            case 'T':
                ScrollDown(Arg(0, 1));
                break;
            case 'm':
                ApplyStyle(args);
                break;
            case 'r':
                SetScrollRegion(Arg(0, 1) - 1, Arg(1, Rows) - 1);
                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
            case 'n' when Raw(0) == 6:
                Reply?.Invoke($"\x1b[{CursorRow + 1};{CursorColumn + 1}R");
                break;
            case 'n' when Raw(0) == 5:
                Reply?.Invoke("\x1b[0n");
                break;
            case 'c':
                Reply?.Invoke("\x1b[?1;0c");
                break;
        }
    }

    private void MoveCursor(int row, int column, bool clampToRegion = false)
    {
        _pendingWrap = false;
        var top = 0;
        var bottom = Rows - 1;
        if (clampToRegion && CursorRow >= _scrollTop && CursorRow <= _scrollBottom)
        {
            top = _scrollTop;
            bottom = _scrollBottom;
        }
        CursorRow = Math.Clamp(row, top, bottom);
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var row = CursorRow + 1; row < Rows; row++)
                    _screen[row] = new TerminalLine(Columns, BlankStyle());
                break;
            case 1:
                EraseLine(1);
                for (var row = 0; row < CursorRow; row++)
                    _screen[row] = new TerminalLine(Columns, BlankStyle());
                break;
            case 2:
                for (var row = 0; row < Rows; row++)
                    _screen[row] = new TerminalLine(Columns, BlankStyle());
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        _pendingWrap = false;
        var line = _screen[CursorRow];
        var (from, to) = mode switch
        {
            0 => (CursorColumn, Columns),
            1 => (0, CursorColumn + 1),
            _ => (0, Columns),
        };
        Array.Fill(line.Cells, new Cell(0, BlankStyle()), from, to - from);
        if (mode != 1)
            line.Wrapped = false;
    }

    private void EraseCells(int count)
    {
        var length = Math.Min(count, Columns - CursorColumn);
        Array.Fill(_screen[CursorRow].Cells, new Cell(0, BlankStyle()), CursorColumn, length);
    }

    private void InsertCells(int count)
    {
        var cells = _screen[CursorRow].Cells;
        count = Math.Min(count, Columns - CursorColumn);
        Array.Copy(cells, CursorColumn, cells, CursorColumn + count, Columns - CursorColumn - count);
        Array.Fill(cells, new Cell(0, BlankStyle()), CursorColumn, count);
    }

    private void DeleteCells(int count)
    {
        var cells = _screen[CursorRow].Cells;
        count = Math.Min(count, Columns - CursorColumn);
        Array.Copy(cells, CursorColumn + count, cells, CursorColumn, Columns - CursorColumn - count);
        Array.Fill(cells, new Cell(0, BlankStyle()), Columns - count, count);
    }

    private void InsertLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
            return;
        var top = _scrollTop;
        _scrollTop = CursorRow;
        ScrollDown(count);
        _scrollTop = top;
        CursorColumn = 0;
    }

    private void DeleteLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
            return;
        var top = _scrollTop;
        _scrollTop = CursorRow;
        // Linhas apagadas no meio da tela não vão para o histórico.
        for (var n = 0; n < Math.Min(count, _scrollBottom - CursorRow + 1); n++)
        {
            Array.Copy(_screen, _scrollTop + 1, _screen, _scrollTop, _scrollBottom - _scrollTop);
            _screen[_scrollBottom] = new TerminalLine(Columns, BlankStyle());
        }
        _scrollTop = top;
        CursorColumn = 0;
    }

    private void SetScrollRegion(int top, int bottom)
    {
        if (top >= bottom || bottom >= Rows)
        {
            top = 0;
            bottom = Rows - 1;
        }
        _scrollTop = top;
        _scrollBottom = bottom;
        MoveCursor(0, 0);
    }

    private void SaveCursor()
    {
        _savedRow = CursorRow;
        _savedColumn = CursorColumn;
        _savedStyle = _style;
    }

    private void RestoreCursor()
    {
        MoveCursor(_savedRow, _savedColumn);
        _style = _savedStyle;
    }

    private void SetPrivateMode(string[] args, bool on)
    {
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "1":
                    ApplicationCursorKeys = on;
                    break;
                case "7":
                    _autoWrap = on;
                    _pendingWrap = false;
                    break;
                case "25":
                    CursorVisible = on;
                    break;
                case "47" or "1047" or "1049":
                    SetAlternateScreen(on);
                    break;
                case "2004":
                    BracketedPaste = on;
                    break;
                case "1000" or "1002" or "1003":
                    MouseTracking = on;
                    break;
                case "1006":
                    SgrMouse = on;
                    break;
            }
        }
    }

    private void SetAlternateScreen(bool on)
    {
        if (on == IsAlternateScreen)
            return;

        if (on)
        {
            SaveCursor();
            _alternate = NewScreen(Columns, Rows);
            _screen = _alternate;
            MoveCursor(0, 0);
        }
        else
        {
            _alternate = null;
            _screen = _main;
            RestoreCursor();
        }
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    private void ApplyStyle(string[] args)
    {
        if (args.Length == 0)
        {
            _style = CellStyle.Default;
            return;
        }

        var foreground = _style.Foreground;
        var background = _style.Background;
        var flags = _style.Flags;
        for (var i = 0; i < args.Length; i++)
        {
            _ = int.TryParse(args[i], out var code);
            switch (code)
            {
                case 0:
                    foreground = -1;
                    background = -1;
                    flags = CellFlags.None;
                    break;
                case 1:
                    flags |= CellFlags.Bold;
                    break;
                case 2:
                    flags |= CellFlags.Dim;
                    break;
                case 3:
                    flags |= CellFlags.Italic;
                    break;
                case 4:
                    flags |= CellFlags.Underline;
                    break;
                case 7:
                    flags |= CellFlags.Inverse;
                    break;
                case 22:
                    flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    flags &= ~CellFlags.Underline;
                    break;
                case 27:
                    flags &= ~CellFlags.Inverse;
                    break;
                case >= 30 and <= 37:
                    foreground = code - 30;
                    break;
                case 38:
                    foreground = ExtendedColor(args, ref i, foreground);
                    break;
                case 39:
                    foreground = -1;
                    break;
                case >= 40 and <= 47:
                    background = code - 40;
                    break;
                case 48:
                    background = ExtendedColor(args, ref i, background);
                    break;
                case 49:
                    background = -1;
                    break;
                case >= 90 and <= 97:
                    foreground = code - 90 + 8;
                    break;
                case >= 100 and <= 107:
                    background = code - 100 + 8;
                    break;
            }
        }
        _style = new CellStyle(foreground, background, flags);
    }

    private static int ExtendedColor(string[] args, ref int i, int current)
    {
        if (i + 1 >= args.Length)
            return current;

        if (args[i + 1] == "5" && i + 2 < args.Length && int.TryParse(args[i + 2], out var index))
        {
            i += 2;
            return Math.Clamp(index, 0, 255);
        }
        if (args[i + 1] == "2" && i + 4 < args.Length
            && byte.TryParse(args[i + 2], out var r) && byte.TryParse(args[i + 3], out var g) && byte.TryParse(args[i + 4], out var b))
        {
            i += 4;
            return CellStyle.TrueColorBit | (r << 16) | (g << 8) | b;
        }
        return current;
    }

    private void Reset()
    {
        _alternate = null;
        _main = NewScreen(Columns, Rows);
        _screen = _main;
        _scrollback.Clear();
        _style = CellStyle.Default;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _autoWrap = true;
        _pendingWrap = false;
        CursorRow = 0;
        CursorColumn = 0;
        CursorVisible = true;
        ApplicationCursorKeys = false;
        BracketedPaste = false;
        MouseTracking = false;
        SgrMouse = false;
    }

    private void ResizeScreen(ref TerminalLine[] screen, int columns, int rows, bool keepScrollback, int cursorRow, out int newCursorRow)
    {
        var lines = new List<TerminalLine>(screen);
        newCursorRow = cursorRow;

        // Ao encolher, as linhas de cima vão para o histórico até o cursor caber.
        while (lines.Count > rows && newCursorRow >= rows)
        {
            if (keepScrollback)
                AddToScrollback(lines[0]);
            lines.RemoveAt(0);
            newCursorRow--;
        }
        if (lines.Count > rows)
            lines.RemoveRange(rows, lines.Count - rows);
        while (lines.Count < rows)
            lines.Add(new TerminalLine(columns, CellStyle.Default));

        foreach (var line in lines)
        {
            if (line.Cells.Length == columns)
                continue;
            var cells = TerminalLine.Blank(columns, CellStyle.Default);
            Array.Copy(line.Cells, cells, Math.Min(columns, line.Cells.Length));
            line.Cells = cells;
        }
        screen = lines.ToArray();
    }

    private static TerminalLine[] NewScreen(int columns, int rows)
    {
        var screen = new TerminalLine[rows];
        for (var i = 0; i < rows; i++)
            screen[i] = new TerminalLine(columns, CellStyle.Default);
        return screen;
    }

    public static int RuneWidth(int rune)
    {
        if (rune < 0x300)
            return 1;
        if (rune is (>= 0x300 and <= 0x36F) or (>= 0x200B and <= 0x200F) or (>= 0xFE00 and <= 0xFE0F))
            return 0;
        if (rune is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3)
            or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE6F) or (>= 0xFF00 and <= 0xFF60)
            or (>= 0xFFE0 and <= 0xFFE6) or (>= 0x1F300 and <= 0x1FAFF) or (>= 0x20000 and <= 0x3FFFD))
            return 2;
        return 1;
    }
}
