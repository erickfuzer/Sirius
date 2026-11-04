using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sirius.Core;

namespace Sirius.Editing;

// O quadro de saturação (horizontal) e brilho (vertical) do seletor de cores.
public sealed class ColorSquare : Control
{
    private const double Corner = 6;
    private const double KnobRadius = 6;
    private static readonly Pen KnobInner = new(Brushes.White, 2);
    private static readonly Pen KnobOuter = new(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 1);

    private double _hue;
    private double _saturation;
    private double _value;

    public ColorSquare() => Cursor = new Cursor(StandardCursorType.Cross);

    public event Action? Changed;

    public double Saturation => _saturation;
    public double Value => _value;

    public void Set(double hue, double saturation, double value)
    {
        (_hue, _saturation, _value) = (hue, saturation, value);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        var rounded = new RoundedRect(area, Corner);
        var hue = CssColor.FromHsv(_hue, 1, 1);

        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(hue.R, hue.G, hue.B)), null, rounded);
        context.DrawRectangle(Gradient(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative), Colors.White, Color.FromArgb(0, 255, 255, 255)), null, rounded);
        context.DrawRectangle(Gradient(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative), Color.FromArgb(0, 0, 0, 0), Colors.Black), null, rounded);

        var knob = new Point(_saturation * area.Width, (1 - _value) * area.Height);
        context.DrawEllipse(null, KnobOuter, knob, KnobRadius + 1, KnobRadius + 1);
        context.DrawEllipse(null, KnobInner, knob, KnobRadius, KnobRadius);
    }

    private static LinearGradientBrush Gradient(RelativePoint start, RelativePoint end, Color from, Color to) => new()
    {
        StartPoint = start,
        EndPoint = end,
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        e.Handled = true;
        Update(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this)
            Update(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void Update(Point point)
    {
        _saturation = Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0, 1);
        _value = 1 - Math.Clamp(point.Y / Math.Max(1, Bounds.Height), 0, 1);
        InvalidateVisual();
        Changed?.Invoke();
    }
}

// A barra de tom ou de transparência do seletor de cores.
public sealed class ColorStrip : Control
{
    private const double Corner = 6;
    private const double CheckerSize = 4;
    private static readonly IBrush CheckerDark = new SolidColorBrush(Color.FromRgb(0xC4, 0xC6, 0xCC));
    private static readonly Pen KnobInner = new(Brushes.White, 2);
    private static readonly Pen KnobOuter = new(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 1);

    private double _position;
    private Color _baseColor = Colors.Black;

    public ColorStrip() => Cursor = new Cursor(StandardCursorType.Hand);

    public event Action? Changed;

    // Barra de tom: mostra o arco-íris. Barra de transparência: mostra a cor sobre um xadrez.
    public bool IsHue { get; set; }

    // De 0 a 1 ao longo da barra.
    public double Position => _position;

    public void Set(double position, Color baseColor)
    {
        (_position, _baseColor) = (position, baseColor);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        var rounded = new RoundedRect(area, Corner);

        if (IsHue)
        {
            var rainbow = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            };
            for (var stop = 0; stop <= 6; stop++)
            {
                var color = CssColor.FromHsv(stop * 60, 1, 1);
                rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(color.R, color.G, color.B), stop / 6d));
            }
            context.DrawRectangle(rainbow, null, rounded);
        }
        else
        {
            using (context.PushGeometryClip(new RectangleGeometry(area) { RadiusX = Corner, RadiusY = Corner }))
            {
                context.FillRectangle(Brushes.White, area);
                for (var x = 0; x * CheckerSize < area.Width; x++)
                {
                    for (var y = 0; y * CheckerSize < area.Height; y++)
                    {
                        if ((x + y) % 2 == 0)
                            context.FillRectangle(CheckerDark, new Rect(x * CheckerSize, y * CheckerSize, CheckerSize, CheckerSize));
                    }
                }
            }

            var fade = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, _baseColor.R, _baseColor.G, _baseColor.B), 0),
                    new GradientStop(Color.FromArgb(255, _baseColor.R, _baseColor.G, _baseColor.B), 1),
                },
            };
            context.DrawRectangle(fade, null, rounded);
        }

        var center = new Point(_position * area.Width, area.Height / 2);
        var radius = area.Height / 2 + 1;
        context.DrawEllipse(null, KnobOuter, center, radius + 1, radius + 1);
        context.DrawEllipse(null, KnobInner, center, radius, radius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        e.Handled = true;
        Update(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this)
            Update(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void Update(Point point)
    {
        _position = Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0, 1);
        InvalidateVisual();
        Changed?.Invoke();
    }
}
