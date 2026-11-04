using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius.Editing;

// Coloca um quadradinho com a cor na frente de cada cor do arquivo.
public sealed class CssColorGenerator : VisualLineElementGenerator
{
    private const double SwatchSize = 10;

    // A margem negativa centraliza com o texto e impede que o quadradinho aumente a altura da linha.
    private static readonly Thickness SwatchMargin = new(0, -3, 4, -3);

    private readonly Func<string?> _path;
    private TextDocument? _document;
    private ITextSourceVersion? _version;
    private string? _scannedPath;
    private IReadOnlyList<CssColorMatch> _matches = [];

    public CssColorGenerator(Func<string?> path) => _path = path;

    public event Action<CssColorMatch>? SwatchPressed;

    // O arquivo inteiro só é relido quando muda: esta consulta roda a cada linha desenhada.
    public IReadOnlyList<CssColorMatch> MatchesFor(TextDocument document)
    {
        var path = _path();
        if (document != _document || document.Version != _version || path != _scannedPath)
        {
            _document = document;
            _version = document.Version;
            _scannedPath = path;
            _matches = CssColors.Supports(path) ? CssColors.Find(document.Text, path) : [];
        }
        return _matches;
    }

    private int FirstAtOrAfter(int offset)
    {
        var (low, high) = (0, _matches.Count);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_matches[middle].Start < offset)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (CurrentContext.Document is not { } document)
            return -1;
        MatchesFor(document);
        var index = FirstAtOrAfter(startOffset);
        return index < _matches.Count ? _matches[index].Start : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var index = FirstAtOrAfter(offset);
        if (index >= _matches.Count || _matches[index].Start != offset)
            return null;

        var match = _matches[index];
        var swatch = BuildSwatch(match);
        swatch.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(swatch).Properties.IsLeftButtonPressed)
                return;
            e.Handled = true;
            SwatchPressed?.Invoke(match);
        };
        return new InlineObjectElement(0, swatch);
    }

    private static Border BuildSwatch(CssColorMatch match)
    {
        var color = match.Color;
        // A cor fica sobre branco: transparente ou escura, o quadradinho continua visível nos dois temas.
        return new Border
        {
            Width = SwatchSize,
            Height = SwatchSize,
            Margin = SwatchMargin,
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(1),
            BorderBrush = Palette.Current.Faint,
            Background = Brushes.White,
            Child = new Border { Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A * 255), color.R, color.G, color.B)) },
        };
    }
}
