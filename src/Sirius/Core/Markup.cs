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
}
