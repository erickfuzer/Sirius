using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Sirius.Theming;

namespace Sirius.Lsp;

// Desenha o sublinhado ondulado embaixo dos erros e avisos.
public sealed class DiagnosticRenderer(TextView view, Func<IReadOnlyList<LspDiagnostic>> source) : IBackgroundRenderer
{
    private const double WaveWidth = 3;
    private const double WaveHeight = 2;

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var document = view.Document;
        if (document is null || !view.VisualLinesValid || view.VisualLines.Count == 0)
            return;

        var first = view.VisualLines[0].FirstDocumentLine.Offset;
        var last = view.VisualLines[^1].LastDocumentLine.EndOffset;
        var palette = Palette.Current;

        foreach (var diagnostic in source())
        {
            var (start, end) = Span(document, diagnostic);
            if (end < first || start > last)
                continue;

            var brush = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => palette.Error,
                DiagnosticSeverity.Warning => palette.Number,
                _ => palette.Muted,
            };
            var pen = new Pen(brush, 1);
            var segment = new TextSegment { StartOffset = start, EndOffset = end };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, segment))
                drawingContext.DrawGeometry(null, pen, Wave(rect));
        }
    }

    // O trecho no documento. Um erro sem largura vira a palavra ou o caractere daquele ponto.
    public static (int Start, int End) Span(TextDocument document, LspDiagnostic diagnostic)
    {
        var start = LspPositions.ToOffset(document, diagnostic.Line, diagnostic.Character);
        var end = LspPositions.ToOffset(document, diagnostic.EndLine, diagnostic.EndCharacter);
        if (end > start)
            return (start, end);

        var line = document.GetLineByOffset(start);
        if (start < line.EndOffset)
            return (start, start + 1);
        return line.Length > 0 ? (start - 1, start) : (start, start);
    }

    private static StreamGeometry Wave(Rect rect)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var y = rect.Bottom - 1;
        var x = rect.Left;
        var up = true;
        context.BeginFigure(new Point(x, y), false);
        while (x < rect.Right)
        {
            x = Math.Min(x + WaveWidth, rect.Right);
            context.LineTo(new Point(x, up ? y - WaveHeight : y));
            up = !up;
        }
        context.EndFigure(false);
        return geometry;
    }
}
