using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius.Editing;

// Paradas de zoom e ajuste à área. Separado para dar para testar sem desenhar nada.
public static class ImageZoom
{
    private const double Tolerance = 0.001;

    private static readonly double[] Stops = [0.05, 0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 32];

    public static double Next(double current, int direction)
    {
        if (direction > 0)
        {
            foreach (var stop in Stops)
            {
                if (stop > current + Tolerance)
                    return stop;
            }
            return Stops[^1];
        }

        for (var i = Stops.Length - 1; i >= 0; i--)
        {
            if (Stops[i] < current - Tolerance)
                return Stops[i];
        }
        return Stops[0];
    }

    // Só reduz: uma imagem pequena continua no tamanho real, sem ficar borrada.
    public static double Fit(double areaWidth, double areaHeight, double imageWidth, double imageHeight)
    {
        if (areaWidth <= 0 || areaHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
            return 1;
        return Math.Min(1, Math.Min(areaWidth / imageWidth, areaHeight / imageHeight));
    }

    // Leva o pixel guardado no arquivo para o lugar em que ele aparece, conforme a orientação da foto.
    public static Matrix OrientationOf(SKEncodedOrigin origin, double width, double height) => origin switch
    {
        SKEncodedOrigin.TopRight => new Matrix(-1, 0, 0, 1, width, 0),
        SKEncodedOrigin.BottomRight => new Matrix(-1, 0, 0, -1, width, height),
        SKEncodedOrigin.BottomLeft => new Matrix(1, 0, 0, -1, 0, height),
        SKEncodedOrigin.LeftTop => new Matrix(0, 1, 1, 0, 0, 0),
        SKEncodedOrigin.RightTop => new Matrix(0, 1, -1, 0, height, 0),
        SKEncodedOrigin.RightBottom => new Matrix(0, -1, -1, 0, height, width),
        SKEncodedOrigin.LeftBottom => new Matrix(0, -1, 1, 0, 0, width),
        _ => Matrix.Identity,
    };
}

// Mostra a imagem da aba: cabe na área ao abrir, e dá para aproximar e arrastar.
public sealed class ImagePreview : Control
{
    private const double SideMargin = 16;
    private const double TopMargin = 16;
    private const double WheelPan = 48;
    private const int CheckerCell = 8;

    // A cápsula flutua sobre a folha: a imagem ajustada termina antes dela.
    private const double BottomMargin = 64;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private Bitmap? _bitmap;
    private double? _zoom;
    private Vector _pan;
    private Point? _dragFrom;
    private Vector _dragPan;
    private double _lastScale;
    private RenderTargetBitmap? _checkerTile;
    private ImageBrush? _checker;
    private Palette? _checkerPalette;
    private double _loadedScale = 1;

    // Animação: os quadros já montados e quanto cada um fica na tela.
    private Bitmap[]? _frames;
    private int[]? _durations;
    private int _frame;
    private int _repetitions;
    private int _restartsLeft;
    private bool _paused;
    private bool _finished;
    private DispatcherTimer? _frameTimer;

    public int FrameCount => _frames?.Length ?? (_bitmap is null ? 0 : 1);
    public int FrameIndex => _frame;
    public bool IsPlaying => _frameTimer?.IsEnabled == true;
    public bool IsAnimated => _frames is not null;
    public bool IsPaused => _paused;

    // Tocou as vezes que o arquivo pede e parou no último quadro.
    public bool IsFinished => _finished;

    // Começou, pausou, continuou ou terminou: o botão de pausar troca o texto.
    public event Action? PlaybackChanged;

    public event Action? ViewChanged;

    // O zoom passou do que a versão reduzida consegue mostrar com nitidez.
    public event Action? NeedsFullImage;

    public ImageInfo? Info { get; private set; }
    public bool IsReduced => _loadedScale < 1;
    public bool IsFit => _zoom is null;
    public double Scale => Info is { } info ? _zoom ?? ImageZoom.Fit(Area.Width, Area.Height, info.Width, info.Height) : 1;

    public string Description => Info is { } info
        ? $"{info.Width} x {info.Height}  {ImageFile.DescribeSize(info.Bytes)}  {Math.Round(Scale * 100)}%"
        : "";

    private Rect Area => new(SideMargin, TopMargin,
        Math.Max(0, Bounds.Width - SideMargin * 2),
        Math.Max(0, Bounds.Height - TopMargin - BottomMargin));

    // Onde a imagem está na tela, já com zoom e deslocamento.
    public Rect ImageRect
    {
        get
        {
            if (Info is not { } info)
                return default;
            var scale = Scale;
            var (width, height) = (info.Width * scale, info.Height * scale);
            var center = Area.Center + _pan;
            // Em posição inteira os pixels ficam nítidos no tamanho real.
            return new Rect(Math.Round(center.X - width / 2), Math.Round(center.Y - height / 2), width, height);
        }
    }

    public bool CanPan => Info is { } info && (info.Width * Scale > Area.Width + 0.5 || info.Height * Scale > Area.Height + 0.5);

    // Assume o bitmap. Numa recarga do mesmo arquivo, mantém o zoom em que a pessoa estava.
    // loadedScale diz quanto do tamanho original o bitmap tem, quando ele veio reduzido.
    public void Show(Bitmap bitmap, ImageInfo info, bool keepView = false, double loadedScale = 1)
    {
        StopAnimation();
        var old = _bitmap;
        _bitmap = bitmap;
        Info = info;
        _loadedScale = loadedScale;
        if (!keepView)
        {
            _zoom = null;
            _pan = default;
        }
        old?.Dispose();
        AfterViewChange();
    }

    // Assume os quadros e troca a imagem parada por eles, no mesmo zoom e lugar. Os quadros vêm no tamanho real.
    public void Animate(IReadOnlyList<Bitmap> frames, IReadOnlyList<int> durations, int repetitions)
    {
        StopAnimation();
        _bitmap?.Dispose();
        _bitmap = null;
        _loadedScale = 1;
        _frames = frames.ToArray();
        _durations = durations.ToArray();
        _frame = 0;
        _repetitions = repetitions;
        _restartsLeft = repetitions;
        _frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(_durations[0]), DispatcherPriority.Render, (_, _) => NextFrame());
        _frameTimer.Start();
        InvalidateVisual();
        PlaybackChanged?.Invoke();
    }

    // Pausa, continua de onde parou ou, depois do fim, toca de novo do começo.
    public void TogglePlayback()
    {
        if (_frames is null || _durations is null || _frameTimer is null)
            return;
        if (_finished)
        {
            _finished = false;
            _frame = 0;
            _restartsLeft = _repetitions;
            _frameTimer.Interval = TimeSpan.FromMilliseconds(_durations[0]);
            _frameTimer.Start();
            InvalidateVisual();
        }
        else if (_paused)
        {
            _paused = false;
            _frameTimer.Start();
        }
        else
        {
            _paused = true;
            _frameTimer.Stop();
        }
        PlaybackChanged?.Invoke();
    }

    private void NextFrame()
    {
        if (_frames is null || _durations is null || _frameTimer is null)
            return;
        var next = _frame + 1;
        if (next >= _frames.Length)
        {
            // Sem repetição pedida, para no último quadro, como no navegador.
            if (_restartsLeft == 0)
            {
                _frameTimer.Stop();
                _finished = true;
                PlaybackChanged?.Invoke();
                return;
            }
            if (_restartsLeft > 0)
                _restartsLeft--;
            next = 0;
        }
        _frame = next;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(_durations[_frame]);
        InvalidateVisual();
    }

    private void StopAnimation()
    {
        _frameTimer?.Stop();
        _frameTimer = null;
        var had = _frames is not null;
        if (_frames is not null)
        {
            foreach (var frame in _frames)
                frame.Dispose();
        }
        _frames = null;
        _durations = null;
        _frame = 0;
        _paused = false;
        _finished = false;
        if (had)
            PlaybackChanged?.Invoke();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frameTimer?.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_paused && !_finished)
            _frameTimer?.Start();
    }

    public void Clear()
    {
        if (Info is null && _bitmap is null && _frames is null)
            return;
        StopAnimation();
        _bitmap?.Dispose();
        _bitmap = null;
        Info = null;
        _zoom = null;
        _pan = default;
        _loadedScale = 1;
        AfterViewChange();
    }

    // Sem memória para a imagem inteira: fica com a reduzida, sem tentar de novo a cada zoom.
    public void KeepReducedImage() => _loadedScale = 1;

    public void ZoomStep(int direction) => ZoomAround(Area.Center, ImageZoom.Next(Scale, direction));

    public void ResetToFit()
    {
        _zoom = null;
        _pan = default;
        AfterViewChange();
    }

    public void ShowActualSize() => ZoomAround(Area.Center, 1);

    public void PanBy(Vector delta)
    {
        _pan += delta;
        AfterViewChange();
    }

    private void ZoomAround(Point point, double scale)
    {
        if (Info is null)
            return;
        // O ponto sob o mouse continua sob o mouse depois do zoom.
        var center = Area.Center + _pan;
        var inImage = (point - center) / Scale;
        _zoom = scale;
        _pan = point - inImage * scale - Area.Center;
        AfterViewChange();
    }

    private void AfterViewChange()
    {
        ClampPan();
        _lastScale = Scale;
        Cursor = CanPan ? HandCursor : null;
        InvalidateVisual();
        ViewChanged?.Invoke();

        // Ajustada à área, a versão reduzida sempre basta. Antes do primeiro layout, a área ainda nem tem tamanho.
        var screenScale = _lastScale * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        if (IsReduced && !IsFit && screenScale > _loadedScale + 0.001)
            NeedsFullImage?.Invoke();
    }

    // Quem cabe fica centralizado, e quem não cabe nunca sai inteiro da área.
    private void ClampPan()
    {
        if (Info is not { } info)
            return;
        var limitX = Math.Max(0, (info.Width * Scale - Area.Width) / 2);
        var limitY = Math.Max(0, (info.Height * Scale - Area.Height) / 2);
        _pan = new Vector(Math.Clamp(_pan.X, -limitX, limitX), Math.Clamp(_pan.Y, -limitY, limitY));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && Info is not null)
        {
            var scaleChanged = Math.Abs(Scale - _lastScale) > 0.0005;
            ClampPan();
            if (scaleChanged)
                AfterViewChange();
        }
    }

    // Mouse

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || Info is null)
            return;

        e.Handled = true;
        if (e.ClickCount == 2)
        {
            if (Math.Abs(Scale - 1) < 0.001 && !IsFit)
                ResetToFit();
            else
                ZoomAround(point.Position, 1);
            return;
        }

        if (CanPan)
        {
            _dragFrom = point.Position;
            _dragPan = _pan;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
            return;
        _pan = _dragPan + (e.GetPosition(this) - from);
        ClampPan();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Info is null)
            return;

        e.Handled = true;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y != 0)
                ZoomAround(e.GetPosition(this), ImageZoom.Next(Scale, Math.Sign(e.Delta.Y)));
            return;
        }

        var delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            ? new Vector(e.Delta.Y * WheelPan, 0)
            : new Vector(e.Delta.X * WheelPan, e.Delta.Y * WheelPan);
        PanBy(delta);
    }

    // Desenho

    public override void Render(DrawingContext context)
    {
        // Sem fundo, o mouse não seria percebido fora da imagem.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var bitmap = _frames?[_frame] ?? _bitmap;
        if (bitmap is null || Info is not { } info)
            return;

        var rect = ImageRect;
        if (info.HasAlpha)
            context.FillRectangle(CheckerBrush(), rect);

        var scale = Scale;
        var quality = scale < 1 ? BitmapInterpolationMode.HighQuality
            : Math.Abs(scale - 1) < 0.001 || scale >= 2 ? BitmapInterpolationMode.None
            : BitmapInterpolationMode.MediumQuality;

        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = quality }))
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(rect.X, rect.Y)))
        using (context.PushTransform(ImageZoom.OrientationOf(info.Origin, info.PixelWidth, info.PixelHeight)))
        {
            var loaded = new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            context.DrawImage(bitmap, loaded, new Rect(0, 0, info.PixelWidth, info.PixelHeight));
        }
    }

    // Xadrez atrás de imagens transparentes. Um ladrilho só, para não desenhar milhares de quadrados.
    private ImageBrush CheckerBrush()
    {
        var palette = Palette.Current;
        if (_checker is not null && _checkerPalette == palette)
            return _checker;

        _checkerTile?.Dispose();
        var size = CheckerCell * 2;
        _checkerTile = new RenderTargetBitmap(new PixelSize(size, size));
        using (var tile = _checkerTile.CreateDrawingContext())
        {
            tile.FillRectangle(palette.CheckerLight, new Rect(0, 0, size, size));
            tile.FillRectangle(palette.CheckerDark, new Rect(0, 0, CheckerCell, CheckerCell));
            tile.FillRectangle(palette.CheckerDark, new Rect(CheckerCell, CheckerCell, CheckerCell, CheckerCell));
        }

        _checkerPalette = palette;
        _checker = new ImageBrush(_checkerTile)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            DestinationRect = new RelativeRect(0, 0, size, size, RelativeUnit.Absolute),
        };
        return _checker;
    }
}
