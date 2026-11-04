using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius;

public partial class MainWindow
{
    private const double ColorCardGap = 6;
    private const double ColorCardEdge = 8;

    private CssColorGenerator _colorSwatches = null!;
    private TextDocument? _pickingIn;
    private int _pickStart;
    private int _pickLength;
    private CssColorFormat _pickFormat;
    private bool _pickEdited;
    private bool _writingPickedColor;
    private double _pickHue;
    private double _pickSaturation;
    private double _pickValue;
    private double _pickAlpha;

    public bool IsColorPickerOpen => ColorCard.IsVisible;
    public string PickedColorText => ColorText.Text ?? "";
    public int CaretOffset => CodeEditor.TextArea.Caret.Offset;

    public double EditorLineHeight(int lineNumber)
    {
        var view = CodeEditor.TextArea.TextView;
        view.EnsureVisualLines();
        return view.GetOrConstructVisualLine(CodeEditor.Document.GetLineByNumber(lineNumber)).Height;
    }

    // Onde o quadradinho de uma cor está na janela. Usado para clicar nele nos testes.
    public Point? SwatchPointInWindow(int offset)
    {
        var view = CodeEditor.TextArea.TextView;
        view.EnsureVisualLines();
        var position = new TextViewPosition(CodeEditor.Document.GetLocation(offset));
        var middle = view.GetVisualPosition(position, VisualYPosition.LineMiddle) - view.ScrollOffset;
        return view.TranslatePoint(new Point(middle.X + 5, middle.Y), this);
    }
    public IReadOnlyList<CssColorMatch> ActiveColorMatches =>
        ActiveDocument is { } document ? _colorSwatches.MatchesFor(document.Document) : [];

    private void InitializeColors()
    {
        _colorSwatches = new CssColorGenerator(() => ActiveDocument?.FullPath);
        _colorSwatches.SwatchPressed += match => OpenColorPicker(match.Start);
        var view = CodeEditor.TextArea.TextView;
        view.ElementGenerators.Add(_colorSwatches);
        view.ScrollOffsetChanged += (_, _) => CloseColorPicker();

        ColorSquareView.Changed += () =>
        {
            (_pickSaturation, _pickValue) = (ColorSquareView.Saturation, ColorSquareView.Value);
            ApplyPickedColor();
        };
        HueStrip.Changed += () =>
        {
            _pickHue = HueStrip.Position * 360;
            ApplyPickedColor();
        };
        AlphaStrip.Changed += () =>
        {
            _pickAlpha = Math.Round(AlphaStrip.Position, 2);
            ApplyPickedColor();
        };

        // Túnel: um clique fora fecha o seletor mesmo que o controle clicado trate o evento.
        AddHandler(PointerPressedEvent, OnPressedAroundColorCard, RoutingStrategies.Tunnel);
        CodeEditor.TextArea.AddHandler(KeyDownEvent, OnKeyDownAtSwatch, RoutingStrategies.Tunnel);
    }

    // O quadradinho ocupa uma coluna, mas nenhum caractere. Sem isto, a seta parava duas vezes
    // no mesmo lugar, e Backspace e Delete não apagavam nada na primeira tentativa.
    private void OnKeyDownAtSwatch(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Back or Key.Delete))
            return;

        var area = CodeEditor.TextArea;
        var offset = area.Caret.Offset;
        if (ActiveColorMatches.All(match => match.Start != offset))
            return;

        var line = area.TextView.GetOrConstructVisualLine(area.Document.GetLineByOffset(offset));
        var before = line.GetVisualColumn(offset - line.FirstDocumentLine.Offset);
        var forward = e.Key is Key.Right or Key.Delete;
        area.Caret.Position = new TextViewPosition(area.Caret.Line, area.Caret.Column, forward ? before + 1 : before);
    }

    private void OnPressedAroundColorCard(object? sender, PointerPressedEventArgs e)
    {
        if (IsColorPickerOpen && !(e.Source is Visual source && (source == ColorCard || ColorCard.IsVisualAncestorOf(source))))
            CloseColorPicker();
    }

    public void OpenColorPicker(int offset)
    {
        CloseColorPicker();
        if (ActiveDocument is not { } document)
            return;

        var found = ActiveColorMatches.Where(match => match.Start == offset).Cast<CssColorMatch?>().FirstOrDefault();
        if (found is not { } match)
            return;

        _pickingIn = document.Document;
        (_pickStart, _pickLength, _pickFormat) = (match.Start, match.Length, match.Format);
        (_pickHue, _pickSaturation, _pickValue) = match.Color.ToHsv();
        _pickAlpha = match.Color.A;
        _pickEdited = false;
        _pickingIn.Changed += OnDocumentChangedWhilePicking;

        ColorText.Text = _pickingIn.GetText(match.Start, match.Length);
        ColorCard.IsVisible = true;
        RefreshColorCard(match.Color);
        PlaceColorCard();
    }

    public void CloseColorPicker()
    {
        if (_pickingIn is not { } document)
            return;

        document.Changed -= OnDocumentChangedWhilePicking;
        if (_pickEdited)
            document.UndoStack.EndUndoGroup();
        _pickingIn = null;
        ColorCard.IsVisible = false;
    }

    // Digitar ou desfazer muda as posições: a cor marcada pode nem existir mais.
    private void OnDocumentChangedWhilePicking(object? sender, DocumentChangeEventArgs e)
    {
        if (!_writingPickedColor)
            CloseColorPicker();
    }

    // Usado também pelos testes, sem precisar arrastar nas barras.
    public void SetPickedColor(double hue, double saturation, double value, double alpha)
    {
        (_pickHue, _pickSaturation, _pickValue, _pickAlpha) = (hue, saturation, value, alpha);
        ApplyPickedColor();
    }

    private void ApplyPickedColor()
    {
        if (_pickingIn is null)
            return;
        var color = CssColor.FromHsv(_pickHue, _pickSaturation, _pickValue, _pickAlpha);
        // Um nome como "red" não descreve a cor nova: passa a hexadecimal.
        if (_pickFormat == CssColorFormat.Named)
            _pickFormat = CssColorFormat.Hex;
        WritePickedColor(color);
        RefreshColorCard(color);
    }

    private void OnColorFormatClick(object? sender, RoutedEventArgs e) => CycleColorFormat();

    public void CycleColorFormat()
    {
        if (_pickingIn is null)
            return;
        _pickFormat = CssColors.NextFormat(_pickFormat);
        var color = CssColor.FromHsv(_pickHue, _pickSaturation, _pickValue, _pickAlpha);
        WritePickedColor(color);
        RefreshColorCard(color);
    }

    private void WritePickedColor(CssColor color)
    {
        var document = _pickingIn!;
        var text = CssColors.Format(color, _pickFormat);
        if (document.GetText(_pickStart, _pickLength) == text)
            return;

        // Um arrasto inteiro vira um só Ctrl Z.
        if (!_pickEdited)
        {
            document.UndoStack.StartUndoGroup();
            _pickEdited = true;
        }

        _writingPickedColor = true;
        try
        {
            document.Replace(_pickStart, _pickLength, text);
        }
        finally
        {
            _writingPickedColor = false;
        }
        _pickLength = text.Length;
    }

    private void RefreshColorCard(CssColor color)
    {
        var solid = Color.FromRgb(color.R, color.G, color.B);
        ColorPreview.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A * 255), color.R, color.G, color.B));
        if (_pickingIn is not null)
            ColorText.Text = _pickingIn.GetText(_pickStart, _pickLength);
        ColorSquareView.Set(_pickHue, _pickSaturation, _pickValue);
        HueStrip.Set(_pickHue / 360, solid);
        AlphaStrip.Set(_pickAlpha, solid);
    }

    private void PlaceColorCard()
    {
        var view = CodeEditor.TextArea.TextView;
        view.EnsureVisualLines();
        var position = new TextViewPosition(_pickingIn!.GetLocation(_pickStart));
        var bottom = view.GetVisualPosition(position, VisualYPosition.LineBottom) - view.ScrollOffset;
        var top = view.GetVisualPosition(position, VisualYPosition.LineTop) - view.ScrollOffset;
        var anchorBottom = view.TranslatePoint(new Point(bottom.X, bottom.Y), SheetHost) ?? default;
        var anchorTop = view.TranslatePoint(new Point(top.X, top.Y), SheetHost) ?? default;

        ColorCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ColorCard.DesiredSize;
        var host = SheetHost.Bounds.Size;

        var x = Math.Clamp(anchorBottom.X - ColorCardGap, ColorCardEdge, Math.Max(ColorCardEdge, host.Width - size.Width - ColorCardEdge));
        var y = anchorBottom.Y + ColorCardGap;
        // Sem espaço embaixo, abre para cima da linha.
        if (y + size.Height > host.Height - ColorCardEdge)
            y = Math.Max(ColorCardEdge, anchorTop.Y - size.Height - ColorCardGap);
        ColorCard.Margin = new Thickness(x, y, 0, 0);
    }
}
