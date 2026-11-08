using AvaloniaEdit.Document;

namespace Sirius.Core;

// Como se comenta em cada lugar: por linha, como //, ou em volta, como <!-- -->.
public sealed record CommentStyle(string? Line, string? BlockStart = null, string? BlockEnd = null)
{
    public static readonly CommentStyle Slashes = new("//");
    public static readonly CommentStyle Hash = new("#");
    public static readonly CommentStyle Dashes = new("--");
    public static readonly CommentStyle Semicolon = new(";");
    public static readonly CommentStyle Css = new(null, "/*", "*/");
    public static readonly CommentStyle Html = new(null, "<!--", "-->");
}

// Comentar, duplicar e mover linhas inteiras, sem depender da janela. Cada operação
// é um único passo de desfazer.
public static class LineEditing
{
    // Arquivos em que HTML, PHP, CSS e JavaScript se misturam, e o comentário depende do trecho.
    private static readonly HashSet<string> MixedMarkup = [".html", ".htm", ".php", ".vue", ".svelte"];

    public static CommentStyle? CommentStyleAt(string? path, string text, int offset)
    {
        var extension = Path.GetExtension(path ?? "").ToLowerInvariant();
        if (MixedMarkup.Contains(extension))
        {
            if (Markup.InPhp(text, offset) || string.CompareOrdinal(text, offset, "<?", 0, 2) == 0)
                return CommentStyle.Slashes;
            if (Markup.InStyle(text, offset))
                return CommentStyle.Css;
            if (Markup.InScript(text, offset))
                return CommentStyle.Slashes;
            return CommentStyle.Html;
        }

        return extension switch
        {
            ".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".tsx" or ".cs" or ".java" or ".kt" or ".swift"
                or ".c" or ".h" or ".cpp" or ".hpp" or ".go" or ".rs" or ".dart" or ".scss" or ".sass" or ".less"
                or ".jsonc" => CommentStyle.Slashes,
            ".py" or ".rb" or ".sh" or ".bash" or ".ps1" or ".psm1" or ".yml" or ".yaml" or ".toml" or ".r"
                or ".pl" or ".conf" or ".env" => CommentStyle.Hash,
            ".sql" or ".lua" => CommentStyle.Dashes,
            ".ini" => CommentStyle.Semicolon,
            ".css" => CommentStyle.Css,
            ".xml" or ".svg" or ".xaml" or ".axaml" or ".csproj" or ".md" => CommentStyle.Html,
            _ => Path.GetFileName(path ?? "").ToLowerInvariant() is ".gitignore" or ".env" or "dockerfile" ? CommentStyle.Hash : null,
        };
    }

    // As linhas tocadas pela seleção. Uma seleção que termina no começo de uma linha não a inclui,
    // como ao selecionar linhas inteiras com o mouse.
    public static (int First, int Last) LinesOf(TextDocument document, int start, int end)
    {
        var first = document.GetLineByOffset(start);
        var last = document.GetLineByOffset(end);
        if (last.LineNumber > first.LineNumber && end == last.Offset)
            last = last.PreviousLine;
        return (first.LineNumber, last.LineNumber);
    }

    // O primeiro caractere visível da linha, onde o comentário começa.
    public static int FirstVisibleOffset(TextDocument document, int lineNumber)
    {
        var line = document.GetLineByNumber(lineNumber);
        return line.Offset + Indentation(document, line);
    }

    // Comenta as linhas, ou descomenta se todas já estiverem comentadas.
    public static void ToggleComment(TextDocument document, int first, int last, CommentStyle style)
    {
        using (document.RunUpdate())
        {
            if (style.Line is { } token)
                ToggleLineComment(document, first, last, token);
            else
                ToggleBlockComment(document, first, last, style.BlockStart!, style.BlockEnd!);
        }
    }

    private static void ToggleLineComment(TextDocument document, int first, int last, string token)
    {
        var lines = Enumerable.Range(first, last - first + 1).Select(document.GetLineByNumber).ToList();
        var filled = lines.Where(line => !IsBlank(document, line)).ToList();
        if (filled.Count == 0)
            filled = lines;

        var commented = filled.All(line => TextAfterIndent(document, line).StartsWith(token, StringComparison.Ordinal));
        if (commented)
        {
            // De baixo para cima, para uma remoção não mudar a posição das seguintes.
            foreach (var line in Enumerable.Reverse(filled))
            {
                var at = line.Offset + Indentation(document, line);
                var length = token.Length;
                if (at + length < line.EndOffset && document.GetCharAt(at + length) == ' ')
                    length++;
                document.Remove(at, length);
            }
            return;
        }

        // Todos na mesma coluna, a do menor recuo, para o bloco continuar alinhado.
        var column = filled.Min(line => Indentation(document, line));
        foreach (var line in Enumerable.Reverse(filled))
            document.Insert(line.Offset + Math.Min(column, line.Length), token + " ");
    }

    private static void ToggleBlockComment(TextDocument document, int first, int last, string open, string close)
    {
        var lines = Enumerable.Range(first, last - first + 1).Select(document.GetLineByNumber).ToList();
        if (lines.All(line => IsBlank(document, line)))
            return;

        var start = lines.First(line => !IsBlank(document, line));
        var end = lines.Last(line => !IsBlank(document, line));
        var from = start.Offset + Indentation(document, start);
        var to = end.EndOffset;
        while (to > from && char.IsWhiteSpace(document.GetCharAt(to - 1)))
            to--;

        var text = document.GetText(from, to - from);
        if (text.StartsWith(open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal) && text.Length >= open.Length + close.Length)
        {
            var closeAt = to - close.Length;
            var closeLength = close.Length;
            if (closeAt > from + open.Length && document.GetCharAt(closeAt - 1) == ' ')
            {
                closeAt--;
                closeLength++;
            }
            document.Remove(closeAt, closeLength);

            var openLength = open.Length;
            if (from + openLength < closeAt && document.GetCharAt(from + openLength) == ' ')
                openLength++;
            document.Remove(from, openLength);
            return;
        }

        document.Insert(to, " " + close);
        document.Insert(from, open + " ");
    }

    // Copia as linhas logo abaixo delas e devolve quantas foram copiadas.
    public static int Duplicate(TextDocument document, int first, int last)
    {
        var start = document.GetLineByNumber(first);
        var end = document.GetLineByNumber(last);
        var text = document.GetText(start.Offset, end.EndOffset - start.Offset);
        document.Insert(end.EndOffset, NewLineAfter(document, end) + text);
        return last - first + 1;
    }

    // Troca o bloco de linhas com a linha de cima ou de baixo. Devolve false quando já está na ponta.
    public static bool Move(TextDocument document, int first, int last, bool up)
    {
        if (up ? first <= 1 : last >= document.LineCount)
            return false;

        var start = document.GetLineByNumber(first);
        var end = document.GetLineByNumber(last);
        var block = document.GetText(start.Offset, end.EndOffset - start.Offset);
        if (up)
        {
            var above = start.PreviousLine;
            var between = document.GetText(above.EndOffset, above.DelimiterLength);
            document.Replace(above.Offset, end.EndOffset - above.Offset, block + between + document.GetText(above.Offset, above.Length));
        }
        else
        {
            var below = end.NextLine;
            var between = document.GetText(end.EndOffset, end.DelimiterLength);
            document.Replace(start.Offset, below.EndOffset - start.Offset, document.GetText(below.Offset, below.Length) + between + block);
        }
        return true;
    }

    // A quebra de linha que o arquivo já usa, para não misturar \n com \r\n.
    private static string NewLineAfter(TextDocument document, DocumentLine line)
    {
        if (line.DelimiterLength > 0)
            return document.GetText(line.EndOffset, line.DelimiterLength);
        if (line.PreviousLine is { DelimiterLength: > 0 } previous)
            return document.GetText(previous.EndOffset, previous.DelimiterLength);
        return Environment.NewLine;
    }

    private static int Indentation(TextDocument document, DocumentLine line)
    {
        var count = 0;
        while (count < line.Length && document.GetCharAt(line.Offset + count) is ' ' or '\t')
            count++;
        return count;
    }

    private static bool IsBlank(TextDocument document, DocumentLine line) => Indentation(document, line) == line.Length;

    private static string TextAfterIndent(TextDocument document, DocumentLine line)
    {
        var indent = Indentation(document, line);
        return document.GetText(line.Offset + indent, line.Length - indent);
    }
}
