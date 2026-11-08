using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius.Editing;

// Pinta o fundo das linhas na comparação: vermelho no lado antigo, verde no novo.
public sealed class DiffLineBackground(Func<IReadOnlyList<DiffRowKind>> kinds, bool isNewSide) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid)
            return;

        var palette = Palette.Current;
        var rows = kinds();
        foreach (var line in textView.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            if (index >= rows.Count || rows[index] == DiffRowKind.Same)
                continue;

            var brush = rows[index] == DiffRowKind.Filler ? palette.Tint : isNewSide ? palette.DiffAdded : palette.DiffRemoved;
            var rect = new Rect(0, line.VisualTop - textView.VerticalOffset, Math.Max(textView.Bounds.Width, textView.WideSpaceWidth), line.Height);
            drawingContext.FillRectangle(brush, rect);
        }
    }
}

// Um contorno no conflito escolhido, quando o arquivo tem vários: os botões valem para ele.
public sealed class DiffFocusOutline(Func<(int First, int Last)?> rows) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || rows() is not { } range)
            return;

        double? top = null;
        double bottom = 0;
        foreach (var line in textView.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            if (index < range.First || index > range.Last)
                continue;
            var y = line.VisualTop - textView.VerticalOffset;
            top ??= y;
            bottom = y + line.Height;
        }
        if (top is not { } start)
            return;

        var width = Math.Max(textView.Bounds.Width, textView.WideSpaceWidth);
        var pen = new Pen(Palette.Current.Accent, 1.5);
        drawingContext.DrawRectangle(null, pen, new Rect(1, start + 0.75, width - 2, bottom - start - 1.5), 3, 3);
    }
}

// Os números das linhas na comparação, que pulam as linhas vazias de alinhamento.
public sealed class DiffNumberMargin : AbstractMargin
{
    private const double MarginWidth = 44;
    private const double FontSize = 11.5;
    private static readonly Typeface Face = new("avares://Sirius/Assets/Fonts#Geist Mono");

    public IReadOnlyList<int?> Numbers { get; set; } = [];

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            oldTextView.ScrollOffsetChanged -= OnVisualLinesChanged;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
            newTextView.ScrollOffsetChanged += OnVisualLinesChanged;
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is null || !view.VisualLinesValid)
            return;

        foreach (var line in view.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            if (index >= Numbers.Count || Numbers[index] is not { } number)
                continue;

            var text = new FormattedText(number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, FontSize, Palette.Current.Faint);
            var y = line.VisualTop - view.VerticalOffset + (line.Height - text.Height) / 2;
            context.DrawText(text, new Point(MarginWidth - 10 - text.Width, y));
        }
    }
}
