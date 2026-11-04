using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Sirius.Theming;

namespace Sirius.Editing;

// Números das linhas e o fio: a linha laranja que desce do topo até onde você está.
public sealed class GutterMargin : AbstractMargin
{
    private const double NumberRight = 40;
    private const double ThreadX = 52;
    private const double GutterWidth = 66;
    private const double NumberSize = 11.5;
    private const double BobWidth = 10;
    private const double BobHeight = 12;

    private static readonly Typeface NumberFace = new("avares://Sirius/Assets/Fonts#Geist Mono");
    private static readonly Typeface ActiveFace = new(new FontFamily("avares://Sirius/Assets/Fonts#Geist Mono"), FontStyle.Normal, FontWeight.SemiBold);

    private readonly TextArea _area;
    private readonly DispatcherTimer _timer;
    private double _threadTop = double.NaN;
    private double _threadTarget;

    public GutterMargin(TextArea area)
    {
        _area = area;
        _area.Caret.PositionChanged += (_, _) => InvalidateVisual();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step());
    }

    // Vai direto para a linha, sem animar. Usado ao trocar de arquivo.
    public void SnapThread()
    {
        _threadTop = double.NaN;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(GutterWidth, 0);

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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var view = TextView;
        if (view is null || Document is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var line = view.GetVisualLineFromVisualTop(e.GetPosition(this).Y + view.VerticalOffset);
        if (line is null)
            return;
        _area.Caret.Location = new TextLocation(line.FirstDocumentLine.LineNumber, 1);
        _area.ClearSelection();
        _area.Focus();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var palette = Palette.Current;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        var view = TextView;
        if (view is null || !view.VisualLinesValid || Document is null)
            return;

        var caretLine = _area.Caret.Line;
        foreach (var line in view.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var active = number == caretLine;
            var text = new FormattedText(
                number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                active ? ActiveFace : NumberFace, NumberSize, active ? palette.Accent : palette.Faint);
            var y = line.VisualTop - view.VerticalOffset + (line.Height - text.Height) / 2;
            context.DrawText(text, new Point(NumberRight - text.Width, y));
        }

        DrawThread(context, view, caretLine, palette);
    }

    private void DrawThread(DrawingContext context, TextView view, int caretLine, Palette palette)
    {
        var lineHeight = view.DefaultLineHeight;
        _threadTarget = view.GetVisualTopByDocumentLine(caretLine) + lineHeight / 2;
        if (double.IsNaN(_threadTop))
            _threadTop = _threadTarget;
        else if (Math.Abs(_threadTop - _threadTarget) > 0.5 && !_timer.IsEnabled)
            _timer.Start();

        var bottom = _threadTop - view.VerticalOffset;
        if (bottom <= 0)
            return;

        context.DrawLine(new Pen(palette.Accent, 1.5), new Point(ThreadX, 0), new Point(ThreadX, Math.Min(bottom, Bounds.Height)));
        if (bottom - BobHeight > Bounds.Height)
            return;

        var bob = new StreamGeometry();
        using (var g = bob.Open())
        {
            g.BeginFigure(new Point(ThreadX - BobWidth / 2, bottom - BobHeight + 2), true);
            g.LineTo(new Point(ThreadX + BobWidth / 2, bottom - BobHeight + 2));
            g.LineTo(new Point(ThreadX, bottom + 2));
            g.EndFigure(true);
        }
        context.DrawGeometry(palette.Accent, null, bob);
    }

    private void Step()
    {
        _threadTop += (_threadTarget - _threadTop) * 0.22;
        if (Math.Abs(_threadTop - _threadTarget) <= 0.5)
        {
            _threadTop = _threadTarget;
            _timer.Stop();
        }
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();
}
