using System.Globalization;
using SkiaSharp;

namespace Sirius.Core;

public sealed class ImageFileException(string message) : Exception(message);

// Width e Height são o tamanho como a imagem aparece, já com a orientação da foto.
public sealed record ImageInfo(int PixelWidth, int PixelHeight, long Bytes, bool HasAlpha, SKEncodedOrigin Origin)
{
    public bool IsSideways => Origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
        or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    public int Width => IsSideways ? PixelHeight : PixelWidth;
    public int Height => IsSideways ? PixelWidth : PixelHeight;
}

public sealed record LoadedImage(byte[] Data, ImageInfo Info);

// Decide o que abre como imagem e lê o cabeçalho. SVG fica de fora de propósito: é código, e continua editável.
public static class ImageFile
{
    public const long MaxBytes = 100 * 1024 * 1024;

    // A memória depende dos pixels, e não do arquivo: um PNG pequeno pode pedir vários gigabytes.
    public const long MaxPixels = 150_000_000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".webp", ".bmp", ".ico",
    };

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    public static LoadedImage Load(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxBytes)
            throw new ImageFileException("Esta imagem tem mais de 100 MB e não pode ser aberta.");

        // Outro programa pode estar com o arquivo aberto, e a Sirius não deve atrapalhar.
        byte[] data;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }

        var info = Inspect(data) ?? throw new ImageFileException("Não foi possível abrir esta imagem.");
        if ((long)info.PixelWidth * info.PixelHeight > MaxPixels)
            throw new ImageFileException("Esta imagem é grande demais para abrir aqui.");
        return new LoadedImage(data, info);
    }

    // Null quando o conteúdo não é uma imagem que dê para mostrar.
    public static ImageInfo? Inspect(byte[] data)
    {
        if (data.Length == 0)
            return null;

        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        return codec is null
            ? null
            : new ImageInfo(codec.Info.Width, codec.Info.Height, data.Length, codec.Info.AlphaType != SKAlphaType.Opaque, codec.EncodedOrigin);
    }

    // Largura para decodificar uma versão que cabe na tela. Null quando a imagem já cabe.
    public static int? ReducedWidth(ImageInfo info, int longestSide)
    {
        var longest = Math.Max(info.PixelWidth, info.PixelHeight);
        return longest <= longestSide ? null : Math.Max(1, (int)(info.PixelWidth * (double)longestSide / longest));
    }

    public static string DescribeSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{Math.Round(bytes / 1024.0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} KB";
        return $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',')} MB";
    }
}
