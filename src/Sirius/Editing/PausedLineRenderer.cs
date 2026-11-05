using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using Sirius.Theming;

namespace Sirius.Editing;

// O fundo da linha em que a depuração parou.
public sealed class PausedLineRenderer(Func<int?> pausedLine) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (pausedLine() is not { } number || !textView.VisualLinesValid)
            return;

        foreach (var line in textView.VisualLines)
        {
            if (line.FirstDocumentLine.LineNumber > number || line.LastDocumentLine.LineNumber < number)
                continue;
            var top = line.VisualTop - textView.VerticalOffset;
            drawingContext.FillRectangle(Palette.Current.Selection, new Rect(0, top, textView.Bounds.Width, line.Height));
        }
    }
}
