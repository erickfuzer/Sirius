using System.Text.RegularExpressions;

namespace Sirius.Core;

// Start e Length marcam o caminho e a posição dentro do texto da linha.
public sealed record ErrorLink(int Start, int Length, string Path, int Line, int Column);

// Encontra referências a arquivos na saída de compiladores e programas.
public static partial class ErrorLinks
{
    // arquivo.cs(12,5) do C# e MSBuild.
    [GeneratedRegex(@"(?<path>(?:[A-Za-z]:)?[^\s:""'()<>|*?]+\.[A-Za-z0-9]+)\((?<line>\d+)(?:,(?<col>\d+))?\)")]
    private static partial Regex Parenthesized();

    // arquivo.ts:12:5 de TypeScript, Rust, Go e GCC.
    [GeneratedRegex(@"(?<path>(?:[A-Za-z]:)?[^\s:""'()<>|*?]+\.[A-Za-z0-9]+):(?<line>\d+)(?::(?<col>\d+))?")]
    private static partial Regex Colon();

    // File "arquivo.py", line 12 do Python.
    [GeneratedRegex(@"File ""(?<path>[^""]+)"", line (?<line>\d+)")]
    private static partial Regex Python();

    public static List<ErrorLink> Find(string text)
    {
        var links = new List<ErrorLink>();
        foreach (var pattern in new[] { Python(), Parenthesized(), Colon() })
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (links.Exists(link => match.Index < link.Start + link.Length && link.Start < match.Index + match.Length))
                    continue;
                if (!int.TryParse(match.Groups["line"].Value, out var line) || line == 0)
                    continue;
                _ = int.TryParse(match.Groups["col"].Value, out var column);
                links.Add(new ErrorLink(match.Index, match.Length, match.Groups["path"].Value, line, Math.Max(column, 1)));
            }
        }
        links.Sort((a, b) => a.Start.CompareTo(b.Start));
        return links;
    }

    // Só devolve o caminho se o arquivo existir de verdade.
    public static string? Resolve(string path, string? root)
    {
        try
        {
            var full = Path.IsPathRooted(path) || root is null ? Path.GetFullPath(path) : Path.GetFullPath(path, root);
            return File.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
