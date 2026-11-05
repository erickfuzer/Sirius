using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius.Editing;

// Números das linhas e o fio: a linha que desce do topo, com uma estrelinha, até onde você está.
public sealed class GutterMargin : AbstractMargin
{
    private const double BaseNumberRight = 40;
    private const double BaseThreadX = 52;
    private const double BaseGutterWidth = 66;
    private const double BaseNumberSize = 11.5;
    private const double StarRadius = 6;
    private const double MarkInset = 7;
    private const double MarkWidth = 2.5;
    private const double DeletedMarkSize = 4;

    private static readonly Typeface NumberFace = new("avares://Sirius/Assets/Fonts#Geist Mono");
    private static readonly Typeface ActiveFace = new(new FontFamily("avares://Sirius/Assets/Fonts#Geist Mono"), FontStyle.Normal, FontWeight.SemiBold);

    private readonly TextArea _area;
    private readonly DispatcherTimer _timer;
    private double _threadTop = double.NaN;
    private double _threadTarget;
    private double _scale = 1;
    private IReadOnlyDictionary<int, GitLineKind>? _lineMarks;

    public IReadOnlyDictionary<int, GitLineKind> LineMarks => _lineMarks ?? new Dictionary<int, GitLineKind>();

    private const double BaseBreakpointX = 9;
    private const double BreakpointRadius = 4;
    private int? _hoverLine;

    // Só em arquivo PHP: é null nos outros, em que clicar continua só movendo o cursor.
    public Func<IReadOnlyList<int>>? Breakpoints { get; set; }
    public event Action<int>? BreakpointClicked;

    public void RedrawBreakpoints() => InvalidateVisual();

    // À esquerda dos números e sobre eles: uma faixa fácil de acertar.
    private bool IsBreakpointZone(Point point) => Breakpoints is not null && point.X <= NumberRight + 2;

    // As linhas que mudaram desde o último commit.
    public void SetLineMarks(IReadOnlyDictionary<int, GitLineKind>? marks)
    {
        _lineMarks = marks;
        InvalidateVisual();
    }

    private double NumberRight => BaseNumberRight * _scale;
    private double ThreadX => BaseThreadX * _scale;
    private double NumberSize => BaseNumberSize * _scale;

    // Acompanha o zoom do editor, para os números continuarem cabendo.
    public void SetScale(double scale)
    {
        _scale = scale;
        _threadTop = double.NaN;
        InvalidateMeasure();
        InvalidateVisual();
    }

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

    protected override Size MeasureOverride(Size availableSize) => new(BaseGutterWidth * _scale, 0);

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
        if (IsBreakpointZone(e.GetPosition(this)))
        {
            BreakpointClicked?.Invoke(line.FirstDocumentLine.LineNumber);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        _area.Caret.Location = new TextLocation(line.FirstDocumentLine.LineNumber, 1);
        _area.ClearSelection();
        _area.Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var view = TextView;
        int? hover = view is not null && IsBreakpointZone(point)
            ? view.GetVisualLineFromVisualTop(point.Y + view.VerticalOffset)?.FirstDocumentLine.LineNumber
            : null;
        if (hover != _hoverLine)
        {
            _hoverLine = hover;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverLine = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var palette = Palette.Current;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        var view = TextView;
        if (view is null || !view.VisualLinesValid || Document is null)
            return;

        var caretLine = _area.Caret.Line;
        var breakpoints = Breakpoints?.Invoke();
        foreach (var line in view.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            DrawBreakpoint(context, palette, breakpoints, number, line.VisualTop - view.VerticalOffset + line.Height / 2);
            var active = number == caretLine;
            var text = new FormattedText(
                number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                active ? ActiveFace : NumberFace, NumberSize, active ? palette.Accent : palette.Faint);
            var y = line.VisualTop - view.VerticalOffset + (line.Height - text.Height) / 2;
            context.DrawText(text, new Point(NumberRight - text.Width, y));
            DrawLineMark(context, palette, number, line.VisualTop - view.VerticalOffset, line.Height);
        }

        DrawThread(context, view, caretLine, palette);
    }

    private void DrawBreakpoint(DrawingContext context, Palette palette, IReadOnlyList<int>? breakpoints, int number, double center)
    {
        var marked = breakpoints?.Contains(number) == true;
        if (!marked && number != _hoverLine)
            return;

        var radius = BreakpointRadius * _scale;
        context.DrawEllipse(marked ? palette.Error : palette.Faint, null, new Point(BaseBreakpointX * _scale, center), radius, radius);
    }

    private void DrawLineMark(DrawingContext context, Palette palette, int number, double top, double height)
    {
        if (_lineMarks is null || !_lineMarks.TryGetValue(number, out var kind))
            return;

        var x = BaseGutterWidth * _scale - MarkInset;
        if (kind == GitLineKind.DeletedAbove)
        {
            var triangle = new StreamGeometry();
            using (var g = triangle.Open())
            {
                g.BeginFigure(new Point(x, top - DeletedMarkSize), true);
                g.LineTo(new Point(x + DeletedMarkSize, top));
                g.LineTo(new Point(x, top + DeletedMarkSize));
                g.EndFigure(true);
            }
            context.DrawGeometry(palette.Error, null, triangle);
            return;
        }

        var brush = kind == GitLineKind.Added ? palette.String : palette.Function;
        context.FillRectangle(brush, new Rect(x, top, MarkWidth, height));
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
        if (bottom - StarRadius > Bounds.Height)
            return;

        context.DrawGeometry(palette.Accent, null, Star(ThreadX, bottom, StarRadius));
    }

    // Quatro pontas com lados curvos, a mesma forma do ícone.
    private static StreamGeometry Star(double x, double y, double radius)
    {
        var near = radius * 0.125;
        var far = radius * 0.5;
        var star = new StreamGeometry();
        using var g = star.Open();
        g.BeginFigure(new Point(x, y - radius), true);
        g.CubicBezierTo(new Point(x + near, y - far), new Point(x + far, y - near), new Point(x + radius, y));
        g.CubicBezierTo(new Point(x + far, y + near), new Point(x + near, y + far), new Point(x, y + radius));
        g.CubicBezierTo(new Point(x - near, y + far), new Point(x - far, y + near), new Point(x - radius, y));
        g.CubicBezierTo(new Point(x - far, y - near), new Point(x - near, y - far), new Point(x, y - radius));
        g.EndFigure(true);
        return star;
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
