using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Platform;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius;

// A aba de imagem. O documento dela fica vazio, então o resto da janela segue tratando como uma aba comum.
public partial class MainWindow
{
    private const int MinReducedSide = 1920;

    private OpenDocument? _shownImage;
    private DateTime _shownImageWrite;
    private int _animationRequest;
    private bool _restoringTabs;

    public bool IsImageShown => ImageHost.IsVisible;
    public string ImageMessageText => ImageMessage.IsVisible ? ImageMessage.Text ?? "" : "";
    public ImagePreview ImagePreviewControl => ImageView;
    public string CaretPositionText => CaretPosition.Text ?? "";
    public bool EditorVisible => CodeEditor.IsVisible;

    private void InitializeImages()
    {
        ImageView.ViewChanged += UpdateCaretPosition;
        ImageView.NeedsFullImage += LoadFullImage;
    }

    // Cobre a maior tela ligada, mesmo que a janela mude para ela depois.
    private int LongestScreenSide()
    {
        var screens = Screens?.All;
        var longest = screens is { Count: > 0 } ? screens.Max(screen => Math.Max(screen.Bounds.Width, screen.Bounds.Height)) : 0;
        return Math.Max(longest, MinReducedSide);
    }

    private void LoadFullImage()
    {
        if (ActiveDocument is not { IsImage: true } document)
            return;
        try
        {
            var loaded = ImageFile.Load(document.FullPath);
            ImageView.Show(new Bitmap(new MemoryStream(loaded.Data)), loaded.Info, keepView: true);
        }
        catch (Exception e) when (e is ImageFileException or IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            ImageView.KeepReducedImage();
            ShowStatus("Não foi possível carregar esta imagem no tamanho real.");
        }
    }

    private void ShowDocumentSurface()
    {
        var image = ActiveDocument is { IsImage: true };
        CodeEditor.IsVisible = ActiveDocument is not null && !image && !IsDiffOpen;
        CodeEditor.IsReadOnly = image;
        ImageHost.IsVisible = image && !IsDiffOpen;
        UpdateSvgPane();
    }

    private void FocusActiveDocument()
    {
        if (ActiveDocument is { IsImage: true })
            ImageView.Focus();
        else if (ActiveDocument is not null)
            CodeEditor.TextArea.Focus();
    }

    // Só a imagem da aba em uso fica na memória. Decodificar de novo ao voltar custa pouco.
    private void RefreshImage()
    {
        if (ActiveDocument is not { IsImage: true } document)
        {
            _shownImage = null;
            _animationRequest++;
            ImageView.Clear();
            ImageMessage.IsVisible = false;
            return;
        }

        // Ao reabrir várias abas, cada uma seria decodificada à toa antes de escolher a ativa.
        if (_restoringTabs || (_shownImage == document && _shownImageWrite == document.LastWriteUtc))
            return;

        var sameTab = _shownImage == document;
        _shownImage = document;
        _shownImageWrite = document.LastWriteUtc;
        _animationRequest++;
        try
        {
            var loaded = ImageFile.Load(document.FullPath);
            // Foto muito maior que a tela gastaria muita memória à toa: a inteira só vem se o zoom pedir.
            var stream = new MemoryStream(loaded.Data);
            if (ImageFile.ReducedWidth(loaded.Info, LongestScreenSide()) is { } width)
                ImageView.Show(Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality), loaded.Info, sameTab, width / (double)loaded.Info.PixelWidth);
            else
                ImageView.Show(new Bitmap(stream), loaded.Info, sameTab);
            ImageMessage.IsVisible = false;
            // O primeiro quadro aparece na hora, e os outros chegam quando terminam de ser montados.
            if (ImageFile.CanAnimate(document.FullPath))
                _ = AnimateAsync(document, loaded.Data);
        }
        catch (Exception e) when (e is ImageFileException or IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            ImageView.Clear();
            ImageMessage.Text = e is ImageFileException ? e.Message : "Não foi possível abrir esta imagem.";
            ImageMessage.IsVisible = true;
        }
    }

    // Monta os quadros fora da thread da interface. Se a pessoa trocou de aba no meio, nada muda.
    private async Task AnimateAsync(OpenDocument document, byte[] data)
    {
        var request = _animationRequest;
        (List<Bitmap> Frames, IReadOnlyList<int> Durations, int Repetitions)? animation;
        try
        {
            animation = await Task.Run(() =>
            {
                using var frames = ImageFile.DecodeFrames(data);
                if (frames is null)
                    return ((List<Bitmap>, IReadOnlyList<int>, int)?)null;
                var bitmaps = new List<Bitmap>(frames.Frames.Count);
                try
                {
                    foreach (var frame in frames.Frames)
                        bitmaps.Add(new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, frame.GetPixels(),
                            new PixelSize(frame.Width, frame.Height), new Vector(96, 96), frame.RowBytes));
                }
                catch
                {
                    bitmaps.ForEach(bitmap => bitmap.Dispose());
                    throw;
                }
                return (bitmaps, frames.Durations, frames.Repetitions);
            });
        }
        catch (ImageFileException e)
        {
            if (request == _animationRequest)
                ShowStatus(e.Message);
            return;
        }
        catch (Exception e) when (e is OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            if (request == _animationRequest)
                ShowStatus("Não foi possível animar esta imagem. Aparece só o primeiro quadro.");
            return;
        }

        if (animation is not { } result)
            return;
        if (request != _animationRequest || _shownImage != document || ActiveDocument != document)
        {
            result.Frames.ForEach(bitmap => bitmap.Dispose());
            return;
        }
        ImageView.Animate(result.Frames, result.Durations, result.Repetitions);
    }

    private void ZoomImage(int direction)
    {
        if (direction == 0)
            ImageView.ResetToFit();
        else
            ImageView.ZoomStep(direction);
    }
}
