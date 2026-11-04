using System.Text.RegularExpressions;

namespace Sirius.Core;

public sealed record SearchQuery(string Text, bool MatchCase = false, bool UseRegex = false);

// Uma linha com resultado. Column e Length apontam a primeira ocorrência da linha.
public sealed record SearchHit(string FullPath, int Line, int Column, int Length, string Before, string Match, string After);

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int FileCount, bool Truncated, bool InvalidQuery);

public static class ProjectSearch
{
    public const int MaxHits = 1000;
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int ContextBefore = 30;
    private const int ContextAfter = 120;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // unsaved traz o texto das abas alteradas, para a busca enxergar o que ainda não foi salvo.
    public static SearchResult Run(
        IEnumerable<string> files, SearchQuery query, IReadOnlyDictionary<string, string> unsaved, CancellationToken token)
    {
        if (!TryBuildRegex(query, out var regex))
            return new SearchResult([], 0, false, true);

        var hits = new List<SearchHit>();
        var fileCount = 0;
        foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var text = unsaved.TryGetValue(path, out var open) ? open : ReadOrNull(path);
            if (text is null)
                continue;

            var before = hits.Count;
            try
            {
                SearchText(path, text, query, regex, hits);
            }
            catch (RegexMatchTimeoutException)
            {
                hits.RemoveRange(before, hits.Count - before);
                continue;
            }

            if (hits.Count > before)
                fileCount++;
            if (hits.Count >= MaxHits)
                return new SearchResult(hits, fileCount, true, false);
        }
        return new SearchResult(hits, fileCount, false, false);
    }

    // Devolve falso só quando a expressão é inválida. Busca comum sai com regex nulo.
    public static bool TryBuildRegex(SearchQuery query, out Regex? regex)
    {
        regex = null;
        if (!query.UseRegex)
            return true;
        try
        {
            var options = RegexOptions.CultureInvariant | (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            regex = new Regex(query.Text, options, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? ReadOrNull(string path) => LoadOrNull(path)?.Text;

    public static LoadedText? LoadOrNull(string path)
    {
        try
        {
            return new FileInfo(path).Length > MaxFileBytes ? null : TextFile.Load(path);
        }
        catch (Exception e) when (e is TextFileException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SearchText(string path, string text, SearchQuery query, Regex? regex, List<SearchHit> hits)
    {
        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var lineNumber = 0;
        var start = 0;
        while (start <= text.Length && hits.Count < MaxHits)
        {
            var end = start;
            while (end < text.Length && text[end] is not ('\n' or '\r'))
                end++;
            lineNumber++;

            var line = text[start..end];
            var (column, length) = FindInLine(line, query.Text, regex, comparison);
            if (column >= 0)
                hits.Add(MakeHit(path, lineNumber, line, column, length));

            if (end >= text.Length)
                break;
            start = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? end + 2 : end + 1;
        }
    }

    private static (int Column, int Length) FindInLine(string line, string text, Regex? regex, StringComparison comparison)
    {
        if (regex is null)
        {
            var index = line.IndexOf(text, comparison);
            return (index, text.Length);
        }

        for (var match = regex.Match(line); match.Success; match = match.NextMatch())
        {
            if (match.Length > 0)
                return (match.Index, match.Length);
        }
        return (-1, 0);
    }

    private static SearchHit MakeHit(string path, int lineNumber, string line, int column, int length)
    {
        var indent = 0;
        while (indent < column && char.IsWhiteSpace(line[indent]))
            indent++;

        var from = Math.Max(indent, column - ContextBefore);
        var matchEnd = column + length;
        var to = Math.Min(line.Length, matchEnd + ContextAfter);
        var prefix = from > indent ? "…" : "";
        var suffix = to < line.Length ? "…" : "";
        return new SearchHit(path, lineNumber, column, length,
            prefix + line[from..column], line[column..matchEnd], line[matchEnd..to] + suffix);
    }
}
