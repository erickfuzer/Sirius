using System.Text;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// Ligar o Xdebug no php.ini de verdade, para páginas servidas por outro programa, como o Apache. Diferente do
// Executar, que liga só durante a execução sem tocar no php.ini, o servidor lê o php.ini ao subir. Por isso a
// Sirius escreve um bloco marcado, guarda antes uma cópia e sabe tirar exatamente o que pôs.
public static partial class PhpIniXdebug
{
    public const int Port = 9003;
    // Só ASCII: o php.ini pode estar em qualquer codificação, e o resto dele é gravado byte a byte.
    private const string Begin = "; >>> Sirius: depuracao (nao edite entre estas marcas)";
    private const string End = "; <<< Sirius: depuracao";

    [GeneratedRegex(@"^Loaded Configuration File:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LoadedLine();

    // O caminho que "php --ini" diz estar em uso. Null quando o PHP roda sem php.ini.
    public static string? ParseLoadedIni(string output)
    {
        var match = LoadedLine().Match(output);
        if (!match.Success)
            return null;
        var path = match.Groups[1].Value;
        return path.StartsWith('(') ? null : path;
    }

    // Sem extensionFile, o PHP já carrega o Xdebug por conta própria, e um segundo daria erro.
    public static string Block(string? extensionFile, string newline = "\n")
    {
        var lines = new List<string> { Begin, "[xdebug]" };
        if (extensionFile is not null)
            lines.Add($"zend_extension=\"{extensionFile.Replace('\\', '/')}\"");
        lines.Add("xdebug.mode=debug");
        lines.Add("xdebug.start_with_request=yes");
        lines.Add("xdebug.client_host=127.0.0.1");
        lines.Add($"xdebug.client_port={Port}");
        // Sem a Sirius ouvindo, a página não pode esperar nem mostrar aviso por isso.
        lines.Add("xdebug.connect_timeout_ms=200");
        lines.Add("xdebug.log_level=0");
        lines.Add(End);
        return string.Join(newline, lines) + newline;
    }

    public static bool HasBlock(string ini) => ini.Contains(Begin, StringComparison.Ordinal);

    // O arquivo do Xdebug que o bloco da Sirius carrega. Null sem bloco, ou quando o PHP já tinha o Xdebug dele.
    public static string? ExtensionIn(string ini)
    {
        var start = ini.IndexOf(Begin, StringComparison.Ordinal);
        var end = start < 0 ? -1 : ini.IndexOf(End, start, StringComparison.Ordinal);
        if (end < 0)
            return null;
        var match = ExtensionLine().Match(ini[start..end]);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("^zend_extension=\"([^\"]+)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionLine();

    // O bloco vai no fim, para valer depois de qualquer outra linha do arquivo. Um bloco antigo é trocado.
    public static string Add(string ini, string? extensionFile)
    {
        var newline = ini.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var without = Remove(ini);
        if (without.Length > 0 && !without.EndsWith('\n'))
            without += newline;
        return without + newline + Block(extensionFile, newline);
    }

    // Tira só o que a Sirius pôs, com a linha vazia que veio junto.
    public static string Remove(string ini)
    {
        var start = ini.IndexOf(Begin, StringComparison.Ordinal);
        if (start < 0)
            return ini;
        var end = ini.IndexOf(End, start, StringComparison.Ordinal);
        if (end < 0)
            return ini;
        end += End.Length;
        while (end < ini.Length && ini[end] is '\r' or '\n')
            end++;
        var before = ini[..start];
        // A linha vazia que separava o bloco do resto.
        if (before.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            before = before[..^2];
        else if (before.EndsWith("\n\n", StringComparison.Ordinal))
            before = before[..^1];
        return before + ini[end..];
    }

    // Latin1 lê e grava cada byte como está: acentos de comentários, em qualquer codificação, voltam iguais.
    public static string Read(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    // Grava com uma cópia do original ao lado, feita só da primeira vez, para nunca guardar uma versão já mexida.
    public static void Write(string path, string text)
    {
        var backup = path + ".antes-da-sirius";
        if (!File.Exists(backup))
            File.Copy(path, backup);
        var temporary = path + ".sirius-tmp";
        File.WriteAllBytes(temporary, Encoding.Latin1.GetBytes(text));
        File.Move(temporary, path, overwrite: true);
    }
}
