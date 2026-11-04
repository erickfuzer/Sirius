using System.Text;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// Um arquivo que vai mudar. Encoding nulo indica uma aba aberta, que é alterada sem gravar.
public sealed record ReplaceItem(string FullPath, string NewText, int Count, Encoding? Encoding);

public sealed record ReplacePlan(IReadOnlyList<ReplaceItem> Items, bool InvalidQuery)
{
    public int Total => Items.Sum(item => item.Count);
}

// Primeiro calcula tudo sem tocar em nada, para a pessoa confirmar sabendo o tamanho da mudança.
public static class ProjectReplace
{
    public static ReplacePlan Plan(
        IEnumerable<string> files, SearchQuery query, string replacement,
        IReadOnlyDictionary<string, string> openTexts, CancellationToken token)
    {
        if (!ProjectSearch.TryBuildRegex(query, out var regex))
            return new ReplacePlan([], true);

        var items = new List<ReplaceItem>();
        foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();

            string text;
            Encoding? encoding = null;
            if (openTexts.TryGetValue(path, out var open))
            {
                text = open;
            }
            else if (ProjectSearch.LoadOrNull(path) is { } loaded)
            {
                text = loaded.Text;
                encoding = loaded.Encoding;
            }
            else
            {
                continue;
            }

            try
            {
                var (newText, count) = ReplaceText(text, query, regex, replacement);
                if (count > 0)
                    items.Add(new ReplaceItem(path, newText, count, encoding));
            }
            catch (RegexMatchTimeoutException)
            {
                // Um arquivo que a expressão não consegue analisar a tempo fica como está.
            }
        }
        return new ReplacePlan(items, false);
    }

    // Trabalha linha a linha, como a busca, e mantém cada quebra de linha original.
    public static (string Text, int Count) ReplaceText(string text, SearchQuery query, Regex? regex, string replacement)
    {
        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var builder = new StringBuilder(text.Length);
        var total = 0;
        var start = 0;
        while (start <= text.Length)
        {
            var end = start;
            while (end < text.Length && text[end] is not ('\n' or '\r'))
                end++;

            var line = text[start..end];
            builder.Append(regex is null
                ? ReplaceLiteral(line, query.Text, replacement, comparison, ref total)
                : ReplaceRegex(line, regex, replacement, ref total));

            if (end >= text.Length)
                break;
            var next = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? end + 2 : end + 1;
            builder.Append(text, end, next - end);
            start = next;
        }
        return (builder.ToString(), total);
    }

    private static string ReplaceLiteral(string line, string find, string replacement, StringComparison comparison, ref int total)
    {
        var index = line.IndexOf(find, comparison);
        if (index < 0)
            return line;

        var builder = new StringBuilder();
        var position = 0;
        while (index >= 0)
        {
            builder.Append(line, position, index - position).Append(replacement);
            position = index + find.Length;
            total++;
            index = line.IndexOf(find, position, comparison);
        }
        return builder.Append(line, position, line.Length - position).ToString();
    }

    private static string ReplaceRegex(string line, Regex regex, string replacement, ref int total)
    {
        var count = 0;
        var result = regex.Replace(line, match =>
        {
            if (match.Length == 0)
                return match.Value;
            count++;
            return match.Result(replacement);
        });
        total += count;
        return result;
    }
}
