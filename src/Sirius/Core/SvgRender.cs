using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using Svg;
using Svg.Skia;

namespace Sirius.Core;

// O desenho de um SVG, já em pixels. Width e Height são o tamanho que o próprio SVG declara.
public sealed record SvgPicture(byte[] Png, int PixelWidth, int PixelHeight, double Width, double Height);

// A prévia do SVG que está sendo editado. Ela nunca busca nada fora do texto: um SVG baixado da internet
// poderia apontar para um servidor e descobrir quem o abriu, ou para uma pasta de rede.
public static partial class SvgRender
{
    // Grande o bastante para o zoom ficar nítido, pequeno o bastante para desenhar a cada pausa na digitação.
    public const int MaxSide = 2048;
    private const int MinSide = 256;

    // Endereços em url(...) que não são do próprio texto, como a fonte de um @font-face.
    // Os embutidos (data:) e os da mesma página (#id) continuam.
    [GeneratedRegex("""url\(\s*(['"]?)(?!\s*(?:data:|#))[^)]*?\1\s*\)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalUrl();

    private static readonly object Gate = new();
    private static bool _closed;

    // A biblioteca vem pronta para buscar imagens e elementos de fora. Desliga uma vez, para o programa todo.
    private static void CloseExternalResources()
    {
        lock (Gate)
        {
            if (_closed)
                return;
            SvgDocument.ResolveExternalImages = ExternalType.None;
            SvgDocument.ResolveExternalElements = ExternalType.None;
            SvgDocument.ResolveExternalXmlEntites = ExternalType.None;
            _closed = true;
        }
    }

    public static string WithoutExternalUrls(string text) => ExternalUrl().Replace(text, "url()");

    public static bool Supports(string? path) => string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);

    // Devolve null quando o texto ainda não forma um desenho, como no meio da digitação.
    public static SvgPicture? Render(string text)
    {
        CloseExternalResources();
        try
        {
            using var svg = new SKSvg();
            if (svg.FromSvg(WithoutExternalUrls(text)) is not { } picture)
                return null;
            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0 || float.IsNaN(bounds.Width) || float.IsNaN(bounds.Height))
                return null;

            // Um ícone de 24 px é desenhado grande, e um mapa enorme é reduzido: o zoom da prévia faz o resto.
            var longest = Math.Max(bounds.Width, bounds.Height);
            var scale = Math.Clamp(MaxSide / longest, MinSide / longest, MaxSide / longest);
            var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));

            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Scale((float)scale);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return new SvgPicture(png.ToArray(), width, height, bounds.Width, bounds.Height);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // Um SVG malformado no meio da edição é o normal: a prévia mostra o último que funcionou.
            return null;
        }
    }

    public static long ByteCount(string text) => Encoding.UTF8.GetByteCount(text);

    public const int MaxChars = 5 * 1024 * 1024;
    private const int RenderStack = 256 * 1024 * 1024;

    // Fora da thread da interface e com pilha folgada: tags aninhadas a fundo, num arquivo de fora,
    // não podem derrubar a Sirius, já que estouro de pilha não se trata com try.
    public static Task<SvgPicture?> RenderAsync(string text)
    {
        if (text.Length > MaxChars)
            return Task.FromResult<SvgPicture?>(null);
        var done = new TaskCompletionSource<SvgPicture?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => done.SetResult(Render(text)), RenderStack) { IsBackground = true, Name = "Prévia do SVG" };
        thread.Start();
        return done.Task;
    }
}
