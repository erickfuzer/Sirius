namespace Sirius.Core;

// Em que trecho de um HTML ou PHP o cursor está: dentro de <?php, <style>, <script> ou de um comentário.
// Um só lugar para o Emmet, o comentar linhas e os atalhos de cada linguagem.
public static class Markup
{
    // O último "open" antes do cursor ainda não foi fechado por um "close".
    public static bool IsOpen(string text, int offset, string open, string close)
    {
        if (offset <= 0)
            return false;
        var before = text.AsSpan(0, Math.Min(offset, text.Length));
        return before.LastIndexOf(open, StringComparison.OrdinalIgnoreCase) > before.LastIndexOf(close, StringComparison.OrdinalIgnoreCase);
    }

    public static bool InPhp(string text, int offset) => IsOpen(text, offset, "<?", "?>");
    public static bool InStyle(string text, int offset) => IsOpen(text, offset, "<style", "</style");
    public static bool InScript(string text, int offset) => IsOpen(text, offset, "<script", "</script");
    public static bool InComment(string text, int offset) => IsOpen(text, offset, "<!--", "-->");

    // Onde começa o conteúdo do <style> aberto, logo depois do ">".
    public static int StyleContentStart(string text, int offset)
    {
        var tag = text.LastIndexOf("<style", Math.Max(0, Math.Min(offset, text.Length) - 1), StringComparison.OrdinalIgnoreCase);
        if (tag < 0)
            return 0;
        var close = text.IndexOf('>', tag);
        return close < 0 || close >= offset ? offset : close + 1;
    }

    // Onde começa o valor do style="..." em que o cursor está, logo depois da aspa, ou -1. Percorre os
    // atributos da tag aberta, pulando o PHP, para não confundir com o valor de outro atributo.
    public static int StyleAttributeStart(string text, int offset)
    {
        if (offset <= 0 || offset > text.Length)
            return -1;
        var tag = offset - 1;
        while (true)
        {
            tag = text.LastIndexOf('<', tag);
            if (tag < 0)
                return -1;
            if (tag + 1 < text.Length && text[tag + 1] == '?')
            {
                if (tag == 0)
                    return -1;
                tag--;
                continue;
            }
            break;
        }

        var i = tag + 1;
        while (i < offset && !char.IsWhiteSpace(text[i]) && text[i] != '>')
            i++;
        while (i < offset)
        {
            var c = text[i];
            if (c == '>')
                return -1;
            if (c == '<' && i + 1 < text.Length && text[i + 1] == '?')
            {
                var end = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                if (end < 0 || end + 2 > offset)
                    return -1;
                i = end + 2;
                continue;
            }
            if (char.IsWhiteSpace(c) || c == '/')
            {
                i++;
                continue;
            }

            var nameStart = i;
            while (i < offset && !char.IsWhiteSpace(text[i]) && text[i] is not ('=' or '>' or '/' or '"' or '\''))
                i++;
            if (i == nameStart)
            {
                i++;
                continue;
            }
            var name = text[nameStart..i];
            var j = i;
            while (j < offset && char.IsWhiteSpace(text[j]))
                j++;
            if (j >= offset || text[j] != '=')
            {
                i = j;
                continue;
            }
            j++;
            while (j < offset && char.IsWhiteSpace(text[j]))
                j++;
            if (j >= offset)
                return -1;

            var quote = text[j];
            if (quote is '"' or '\'')
            {
                var close = ClosingQuote(text, j + 1, quote);
                if (close < 0 || close >= offset)
                    return name.Equals("style", StringComparison.OrdinalIgnoreCase) ? j + 1 : -1;
                i = close + 1;
            }
            else
            {
                while (j < offset && !char.IsWhiteSpace(text[j]) && text[j] != '>')
                    j++;
                if (j >= offset)
                    return -1;
                i = j;
            }
        }
        return -1;
    }

    // A aspa que fecha o valor, sem contar as de dentro de um <?= ... ?>.
    private static int ClosingQuote(string text, int from, char quote)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == quote)
                return i;
            if (text[i] == '<' && i + 1 < text.Length && text[i + 1] == '?')
            {
                var end = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    return -1;
                i = end + 1;
            }
        }
        return -1;
    }
}
