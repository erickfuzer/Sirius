using System.Text;

namespace Sirius.Core;

public sealed class TextFileException(string message) : Exception(message);

public sealed record LoadedText(string Text, Encoding Encoding, DateTime LastWriteUtc);

// Lê e grava mantendo a codificação original, para salvar nunca estragar acentos.
public static class TextFile
{
    public const long MaxBytes = 50 * 1024 * 1024;
    private const int BinaryProbeBytes = 8192;

    public static LoadedText Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
            throw new TextFileException("Este arquivo tem mais de 50 MB e não pode ser aberto.");

        var decoded = Decode(File.ReadAllBytes(path))
            ?? throw new TextFileException("Este arquivo é binário e não pode ser aberto como texto.");
        return new LoadedText(decoded.Text, decoded.Encoding, info.LastWriteTimeUtc);
    }

    // Null quando o conteúdo é binário.
    public static (string Text, Encoding Encoding)? Decode(byte[] bytes)
    {
        var encoding = DetectBom(bytes);
        if (encoding is null)
        {
            if (LooksBinary(bytes))
                return null;
            encoding = IsValidUtf8(bytes) ? new UTF8Encoding(false) : Encoding.Latin1;
        }

        var skip = encoding.GetPreamble().Length;
        return (encoding.GetString(bytes, skip, bytes.Length - skip), encoding);
    }

    public static DateTime Save(string path, string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);

        // Grava primeiro em um arquivo temporário: uma falha no meio não destrói o original.
        var temp = path + ".sirius-tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(preamble);
                stream.Write(body);
            }

            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return File.GetLastWriteTimeUtc(path);
    }

    private static Encoding? DetectBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new UnicodeEncoding(false, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new UnicodeEncoding(true, true);
        return null;
    }

    private static bool LooksBinary(byte[] bytes)
    {
        var probe = Math.Min(bytes.Length, BinaryProbeBytes);
        return Array.IndexOf(bytes, (byte)0, 0, probe) >= 0;
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(false, true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
