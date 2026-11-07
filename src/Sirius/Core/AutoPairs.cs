using System.Text.RegularExpressions;

namespace Sirius.Core;

// As regras de fechar aspas, parênteses e tags enquanto se digita, sem depender da janela.
public static partial class AutoPairs
{
    private const int MaxTagLength = 2000;
    private const string ClosesBefore = ";:.,=}])>`";

    [GeneratedRegex(@"^<([A-Za-z][\w:.-]*)(\s[^<>]*)?>$")]
    private static partial Regex OpeningTag();

    // Em texto corrido, aspas e parênteses são só pontuação.
    public static bool Supports(string? path) =>
        Path.GetExtension(path ?? "").ToLowerInvariant() is not (".txt" or ".log" or ".csv");

    public static char? CloserFor(char opener) => opener switch
    {
        '(' => ')',
        '[' => ']',
        '{' => '}',
        '"' or '\'' or '`' => opener,
        _ => null,
    };

    // Fecha só quando depois do cursor não há algo a que o par poderia estar se juntando.
    public static bool ShouldClose(string text, int caret, char opener)
    {
        if (caret < text.Length && !char.IsWhiteSpace(text[caret]) && !ClosesBefore.Contains(text[caret]))
            return false;
        if (opener is not ('"' or '\'' or '`'))
            return true;

        // Em d'água, a aspa é apóstrofo. Em \", está escapada.
        if (caret > 0 && (char.IsLetterOrDigit(text[caret - 1]) || text[caret - 1] is '_' or '\\' || text[caret - 1] == opener))
            return false;
        return !IsInStringOrComment(text, caret);
    }

    // Olha só a linha atual: aspas e comentários de várias linhas não mudam quase nada aqui.
    private static bool IsInStringOrComment(string text, int caret)
    {
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        char? quote = null;
        for (var i = lineStart; i < caret; i++)
        {
            var c = text[i];
            if (quote is { } open)
            {
                if (c == '\\')
                    i++;
                else if (c == open)
                    quote = null;
            }
            else if (c is '"' or '\'' or '`')
            {
                quote = c;
            }
            else if (c == '/' && i + 1 < caret && (text[i + 1] == '*' || (text[i + 1] == '/' && (i == 0 || text[i - 1] != ':'))))
            {
                // O ":" é o de http://, que não começa comentário nenhum.
                return true;
            }
        }
        return quote is not null;
    }

    // O Enter entre {} ou <div></div> abre uma linha no meio, em vez de deixar o fechamento grudado.
    public static bool OpensOnEnter(string text, int caret)
    {
        if (caret <= 0 || caret >= text.Length)
            return false;
        var (before, after) = (text[caret - 1], text[caret]);
        if ((before, after) is ('{', '}') or ('[', ']') or ('(', ')'))
            return true;
        if (before != '>' || after != '<' || caret + 1 >= text.Length || text[caret + 1] != '/' || caret < 2 || text[caret - 2] == '/')
            return false;
        var open = text.LastIndexOf('<', caret - 2);
        return open >= 0 && text[open + 1] != '/';
    }

    // O fechamento da tag que acabou de ser escrita, com o ">" logo antes do cursor. Null se não couber um.
    public static string? TagToClose(string text, int caret)
    {
        if (caret < 3 || caret > text.Length || text[caret - 1] != '>' || text[caret - 2] == '/')
            return null;

        var start = text.LastIndexOf('<', caret - 2);
        if (start < 0 || caret - start > MaxTagLength)
            return null;

        var tag = OpeningTag().Match(text[start..caret]);
        if (!tag.Success || HasOpenQuote(tag.Groups[2].Value) || !Emmet.IsPlainMarkup(text, start))
            return null;

        var name = tag.Groups[1].Value;
        if (Emmet.IsVoidTag(name))
            return null;

        var closing = "</" + name;
        var next = caret;
        while (next < text.Length && char.IsWhiteSpace(text[next]))
            next++;
        return string.CompareOrdinal(text, next, closing, 0, closing.Length) == 0 ? null : closing + ">";
    }

    // Com aspa aberta, o ">" ainda está dentro do valor de um atributo.
    private static bool HasOpenQuote(string attributes)
    {
        char? quote = null;
        foreach (var c in attributes)
        {
            if (quote == c)
                quote = null;
            else if (quote is null && c is '"' or '\'')
                quote = c;
        }
        return quote is not null;
    }
}
