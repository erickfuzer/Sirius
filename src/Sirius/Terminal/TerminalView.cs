using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius.Terminal;

// Desenha a tela do terminal e recebe o teclado e o mouse.
public sealed class TerminalView : Control
{
    private const double FontSize = 12.5;
    private const int WheelLines = 3;
    private static readonly FontFamily Mono = new("avares://Sirius/Assets/Fonts#Geist Mono");
    private static readonly Typeface Regular = new(Mono);
    private static readonly Typeface Bold = new(Mono, FontStyle.Normal, FontWeight.SemiBold);

    private readonly Dictionary<int, IBrush> _brushes = new();
    private TerminalSession? _session;
    private TerminalScheme _scheme = TerminalScheme.For(Palette.Current);
    private double _cellWidth = 7.5;
    private double _cellHeight = 17;
    private int _scrollOffset;
    private (int Line, int Column)? _selectionStart;
    private (int Line, int Column)? _selectionEnd;
    private bool _dragging;
    private bool _dragMoved;
    private HoverLink? _hover;

    private sealed record HoverLink(int FirstLine, ErrorLink Link);

    public TerminalView()
    {
        Focusable = true;
        MeasureCell();
    }

    public int GridColumns => Math.Max(20, (int)((double.IsNaN(Width) ? Bounds.Width : Width) / _cellWidth));
    public int GridRows => Math.Max(3, (int)((double.IsNaN(Height) ? Bounds.Height : Height) / _cellHeight));

    // Encontra o arquivo de um erro no disco. Null quando o caminho não existe.
    public Func<string, string?>? ResolveLink { get; set; }

    public event Action<string, int, int>? LinkActivated;

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
                _session.Changed -= OnSessionChanged;
            _session = value;
            if (_session is not null)
            {
                _session.Changed += OnSessionChanged;
                SyncSize();
            }
            _scrollOffset = 0;
            ClearSelection();
            InvalidateVisual();
        }
    }

    public void ApplyScheme(TerminalScheme scheme)
    {
        _scheme = scheme;
        _brushes.Clear();
        InvalidateVisual();
    }

    public void SyncSize()
    {
        if (_session is null)
            return;
        if (_session.Buffer.Columns != GridColumns || _session.Buffer.Rows != GridRows)
            _session.Resize(GridColumns, GridRows);
    }

    // O centro de uma célula da tela, em coordenadas deste controle.
    public Point PointForCell(int line, int column)
    {
        var buffer = _session!.Buffer;
        var top = Math.Max(0, buffer.TotalLines - buffer.Rows - _scrollOffset);
        return new Point((column + 0.5) * _cellWidth, (line - top + 0.5) * _cellHeight);
    }

    private void OnSessionChanged()
    {
        InvalidateVisual();
    }

    private void MeasureCell()
    {
        var sample = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Regular, FontSize, Brushes.White);
        if (sample.WidthIncludingTrailingWhitespace > 0)
            _cellWidth = sample.WidthIncludingTrailingWhitespace / 10;
        if (sample.Height > 0)
            _cellHeight = Math.Ceiling(sample.Height * 1.12);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SyncSize();
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var buffer = _session?.Buffer;
        if (buffer is null)
            return;

        var top = Math.Max(0, buffer.TotalLines - buffer.Rows - _scrollOffset);
        var rows = Math.Min(buffer.Rows, buffer.TotalLines - top);
        for (var row = 0; row < rows; row++)
            DrawLine(context, buffer, top + row, row);

        if (_hover is not null)
            DrawHover(context, buffer, top, rows);
        DrawCursor(context, buffer, top);
    }

    private void DrawLine(DrawingContext context, TerminalBuffer buffer, int lineIndex, int row)
    {
        var cells = buffer.GetLine(lineIndex).Cells;
        var y = row * _cellHeight;
        var text = new StringBuilder();
        var column = 0;
        while (column < cells.Length)
        {
            var style = cells[column].Style;
            var start = column;
            text.Clear();
            while (column < cells.Length && cells[column].Style == style)
            {
                var rune = cells[column].Rune;
                if (rune > 0)
                    text.Append(char.ConvertFromUtf32(rune));
                else if (rune == 0)
                    text.Append(' ');
                column++;
            }

            var rect = new Rect(start * _cellWidth, y, (column - start) * _cellWidth, _cellHeight);
            var (foreground, background) = Colors(style);
            if (background is not null)
                context.FillRectangle(background, rect);
            var value = text.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                var typeface = style.Flags.HasFlag(CellFlags.Bold) ? Bold : Regular;
                var formatted = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground);
                context.DrawText(formatted, new Point(rect.X, y + (_cellHeight - formatted.Height) / 2));
                if (style.Flags.HasFlag(CellFlags.Underline))
                    context.DrawLine(new Pen(foreground), new Point(rect.Left, rect.Bottom - 1), new Point(rect.Right, rect.Bottom - 1));
            }
        }

        DrawSelection(context, lineIndex, row, cells.Length);
    }

    private void DrawSelection(DrawingContext context, int lineIndex, int row, int width)
    {
        if (OrderedSelection() is not var (from, to))
            return;
        if (lineIndex < from.Line || lineIndex > to.Line)
            return;

        var first = lineIndex == from.Line ? from.Column : 0;
        var last = lineIndex == to.Line ? to.Column : width;
        if (last > first)
            context.FillRectangle(_scheme.Selection, new Rect(first * _cellWidth, row * _cellHeight, (last - first) * _cellWidth, _cellHeight));
    }

    private void DrawCursor(DrawingContext context, TerminalBuffer buffer, int top)
    {
        if (!buffer.CursorVisible || _scrollOffset != 0 || _session is not { IsRunning: true })
            return;
        var row = buffer.CursorLine - top;
        var rect = new Rect(buffer.CursorColumn * _cellWidth, row * _cellHeight, _cellWidth, _cellHeight);
        if (IsFocused)
            context.FillRectangle(_scheme.Cursor, rect.WithWidth(2));
        else
            context.DrawRectangle(null, new Pen(_scheme.Cursor), rect.Deflate(0.5));
    }

    private void DrawHover(DrawingContext context, TerminalBuffer buffer, int top, int rows)
    {
        var pen = new Pen(_scheme.Cursor);
        var line = _hover!.FirstLine;
        var offset = 0;
        var end = _hover.Link.Start + _hover.Link.Length;
        while (line < buffer.TotalLines && offset < end)
        {
            var width = buffer.GetLine(line).Cells.Length;
            var from = Math.Max(_hover.Link.Start - offset, 0);
            var to = Math.Min(end - offset, width);
            var row = line - top;
            if (to > from && row >= 0 && row < rows)
            {
                var y = (row + 1) * _cellHeight - 1;
                context.DrawLine(pen, new Point(from * _cellWidth, y), new Point(to * _cellWidth, y));
            }
            offset += width;
            line++;
        }
    }

    private (IBrush Foreground, IBrush? Background) Colors(CellStyle style)
    {
        var foreground = BrushFor(style.Foreground) ?? _scheme.Foreground;
        var background = BrushFor(style.Background);
        if (style.Flags.HasFlag(CellFlags.Inverse))
            (foreground, background) = (background ?? _scheme.Background, foreground);
        if (style.Flags.HasFlag(CellFlags.Dim) && foreground is ISolidColorBrush solid)
            foreground = new SolidColorBrush(solid.Color, 0.65);
        return (foreground, background);
    }

    private IBrush? BrushFor(int color)
    {
        if (color < 0)
            return null;
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(_scheme.Resolve(color)).ToImmutable();
            _brushes[color] = brush;
        }
        return brush;
    }

    // Teclado

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is not { } session)
            return;

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (shift && e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            Scroll((e.Key == Key.PageUp ? 1 : -1) * Math.Max(1, session.Buffer.Rows - 1));
            return;
        }

        if (control && e.Key == Key.C && (shift || HasSelection))
        {
            e.Handled = true;
            _ = CopySelectionAsync();
            return;
        }

        if (control && e.Key == Key.V)
        {
            e.Handled = true;
            _ = PasteAsync();
            return;
        }

        var sequence = TerminalKeys.Encode(e.Key, e.KeyModifiers, session.Buffer.ApplicationCursorKeys);
        if (sequence is null)
            return;
        e.Handled = true;
        SendInput(sequence);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text))
        {
            e.Handled = true;
            SendInput(e.Text);
        }
    }

    private void SendInput(string text)
    {
        _scrollOffset = 0;
        ClearSelection();
        InvalidateVisual();
        _session?.Write(text);
    }

    private bool HasSelection => OrderedSelection() is var (from, to) && from != to;

    private async Task CopySelectionAsync()
    {
        var text = SelectedText();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (text.Length > 0 && clipboard is not null)
            await clipboard.SetTextAsync(text);
        ClearSelection();
        InvalidateVisual();
    }

    private async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || _session is null)
            return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        SendInput(_session.Buffer.BracketedPaste ? $"\x1b[200~{text}\x1b[201~" : text);
    }

    // Mouse

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        Scroll((int)Math.Round(e.Delta.Y * WheelLines));
    }

    private void Scroll(int lines)
    {
        if (_session is null)
            return;
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, _session.Buffer.ScrollbackCount);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _session is null)
            return;

        var position = PositionAt(e.GetPosition(this));
        _selectionStart = position;
        _selectionEnd = position;
        _dragging = true;
        _dragMoved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_session is null)
            return;

        var position = PositionAt(e.GetPosition(this));
        if (_dragging)
        {
            _dragMoved |= position != _selectionStart;
            _selectionEnd = position;
            InvalidateVisual();
            return;
        }

        var hover = LinkAt(position);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;

        _dragging = false;
        e.Pointer.Capture(null);
        if (_dragMoved)
            return;

        ClearSelection();
        InvalidateVisual();
        if (LinkAt(PositionAt(e.GetPosition(this))) is { } hover && ResolveLink?.Invoke(hover.Link.Path) is { } path)
            LinkActivated?.Invoke(path, hover.Link.Line, hover.Link.Column);
    }

    private (int Line, int Column) PositionAt(Point point)
    {
        var buffer = _session!.Buffer;
        var top = Math.Max(0, buffer.TotalLines - buffer.Rows - _scrollOffset);
        var line = Math.Clamp(top + (int)(point.Y / _cellHeight), 0, buffer.TotalLines - 1);
        var column = Math.Clamp((int)Math.Round(point.X / _cellWidth), 0, buffer.Columns);
        return (line, column);
    }

    private HoverLink? LinkAt((int Line, int Column) position)
    {
        var buffer = _session!.Buffer;
        var (text, first) = buffer.LogicalLine(position.Line);
        if (text.Length == 0)
            return null;

        var offset = position.Column;
        for (var line = first; line < position.Line; line++)
            offset += buffer.GetLine(line).Cells.Length;

        foreach (var link in ErrorLinks.Find(text))
        {
            if (offset >= link.Start && offset < link.Start + link.Length && ResolveLink?.Invoke(link.Path) is not null)
                return new HoverLink(first, link);
        }
        return null;
    }

    private void ClearSelection()
    {
        _selectionStart = null;
        _selectionEnd = null;
    }

    private ((int Line, int Column) From, (int Line, int Column) To)? OrderedSelection()
    {
        if (_selectionStart is not { } a || _selectionEnd is not { } b)
            return null;
        return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    }

    private string SelectedText()
    {
        if (_session is null || OrderedSelection() is not var (from, to))
            return "";

        var buffer = _session.Buffer;
        var builder = new StringBuilder();
        for (var line = from.Line; line <= to.Line; line++)
        {
            var row = buffer.GetLine(line);
            var first = line == from.Line ? from.Column : 0;
            var last = line == to.Line ? Math.Min(to.Column, row.Cells.Length) : row.Cells.Length;
            var part = new StringBuilder();
            for (var column = first; column < last; column++)
            {
                var rune = row.Cells[column].Rune;
                if (rune > 0)
                    part.Append(char.ConvertFromUtf32(rune));
                else if (rune == 0)
                    part.Append(' ');
            }
            builder.Append(row.Wrapped && line < to.Line ? part.ToString() : part.ToString().TrimEnd());
            if (!row.Wrapped && line < to.Line)
                builder.Append(Environment.NewLine);
        }
        return builder.ToString();
    }
}
