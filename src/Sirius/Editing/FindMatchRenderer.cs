using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius.Editing;

// Contorna no editor cada resultado da busca no arquivo. O atual é a própria seleção, por cima.
public sealed class FindMatchRenderer(Func<IReadOnlyList<FindMatch>> matches) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0 || textView.Document is not { } document)
            return;
        var list = matches();
        if (list.Count == 0)
            return;

        var first = textView.VisualLines[0].FirstDocumentLine.Offset;
        var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var palette = Palette.Current;
        var pen = new Pen(palette.FindMatchBorder, 1);
        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 3 };
        var any = false;
        // Só os que aparecem na tela: começa pelo primeiro que termina depois do topo.
        for (var i = Math.Max(0, FindInText.FirstAtOrAfter(list, first) - 1); i < list.Count; i++)
        {
            var match = list[i];
            if (match.Offset > last)
                break;
            if (match.End < first || match.End > document.TextLength)
                continue;
            builder.AddSegment(textView, new TextSegment { StartOffset = match.Offset, Length = match.Length });
            builder.CloseFigure();
            any = true;
        }
        if (any && builder.CreateGeometry() is { } geometry)
            drawingContext.DrawGeometry(palette.FindMatch, pen, geometry);
    }
}
