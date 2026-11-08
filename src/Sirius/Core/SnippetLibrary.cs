using System.Text.Json;

namespace Sirius.Core;

// Um atalho de uma linguagem, como "foreach" no PHP: o prefixo que se digita e o corpo no formato de trecho.
public sealed record SnippetEntry(string Prefix, string Body, string Name);

// Os atalhos que o VS Code traz de fábrica, copiados dos arquivos dele (licença MIT, em Assets\Snippets).
// CSS, Python, Go, Rust e JSON não têm nenhum lá: nelas, quem ajuda é o Emmet ou o servidor de linguagem.
public static class SnippetLibrary
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["php"] = "php.code-snippets",
        ["javascript"] = "javascript.code-snippets",
        ["typescript"] = "typescript.code-snippets",
        ["markdown"] = "markdown.code-snippets",
        ["csharp"] = "csharp.code-snippets",
        ["c"] = "c.code-snippets",
        ["cpp"] = "cpp.code-snippets",
    };

    private static readonly Dictionary<string, IReadOnlyList<SnippetEntry>> Loaded = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    // Lê um arquivo embutido no programa. Os do VS Code aceitam comentários e vírgula sobrando.
    public static Stream? Resource(string file) =>
        typeof(SnippetLibrary).Assembly.GetManifestResourceStream("Snippets." + file);

    public static IReadOnlyList<SnippetEntry> For(string language)
    {
        lock (Gate)
        {
            if (Loaded.TryGetValue(language, out var cached))
                return cached;
            var entries = Files.TryGetValue(language, out var file) ? Load(file) : [];
            Loaded[language] = entries;
            return entries;
        }
    }

    private static List<SnippetEntry> Load(string file)
    {
        using var stream = Resource(file);
        if (stream is null)
            return [];
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var entries = new List<SnippetEntry>();
        foreach (var item in document.RootElement.EnumerateObject())
        {
            var value = item.Value;
            if (!value.TryGetProperty("prefix", out var prefixValue) || !value.TryGetProperty("body", out var bodyValue))
                continue;
            var body = bodyValue.ValueKind == JsonValueKind.Array
                ? string.Join("\n", bodyValue.EnumerateArray().Select(line => line.GetString()))
                : bodyValue.GetString() ?? "";
            var prefixes = prefixValue.ValueKind == JsonValueKind.Array
                ? prefixValue.EnumerateArray().Select(p => p.GetString() ?? "")
                // "keyval,kvp" no PHP: dois prefixos numa string só.
                : (prefixValue.GetString() ?? "").Split(',');
            foreach (var prefix in prefixes.Select(p => p.Trim()).Where(p => p.Length > 0))
                entries.Add(new SnippetEntry(prefix, body, item.Name));
        }
        return entries;
    }

    // A linguagem dos atalhos no cursor: PHP dentro de <?php, JavaScript dentro de <script>.
    public static string? LanguageAt(string? path, string text, int offset)
    {
        var extension = Path.GetExtension(path ?? "").ToLowerInvariant();
        switch (extension)
        {
            case ".php" or ".phtml":
                if (Markup.InPhp(text, offset))
                    return "php";
                return Markup.InScript(text, offset) ? "javascript" : null;
            case ".html" or ".htm" or ".vue" or ".svelte":
                return Markup.InScript(text, offset) ? "javascript" : null;
            case ".js" or ".mjs" or ".cjs" or ".jsx":
                return "javascript";
            case ".ts" or ".tsx" or ".mts" or ".cts":
                return "typescript";
            case ".md" or ".markdown":
                return "markdown";
            case ".cs":
                return "csharp";
            case ".c" or ".h":
                return "c";
            case ".cpp" or ".hpp" or ".cc" or ".cxx" or ".hh":
                return "cpp";
            default:
                return null;
        }
    }

    // A palavra logo antes do cursor, que escolhe os atalhos. Depois de "$", "." ou "->" é um nome do código,
    // como uma variável do PHP ou um método, e ali atalho nenhum faz sentido.
    public static (int Start, string Word)? WordBefore(string text, int caret)
    {
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            start--;
        if (start > 0 && text[start - 1] == '#')
            start--;
        if (start > 0 && text[start - 1] is '$' or '.' or '>' or ':' or '\\')
            return null;
        return (start, text[start..caret]);
    }

    // Como no VS Code: as letras digitadas aparecem em ordem no prefixo, e a primeira bate. "fe" acha "foreach".
    public static bool Matches(string prefix, string word)
    {
        if (word.Length == 0)
            return true;
        if (prefix.Length == 0 || char.ToLowerInvariant(prefix[0]) != char.ToLowerInvariant(word[0]))
            return false;
        var at = 0;
        foreach (var c in word)
        {
            at = prefix.IndexOf(c.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;
            at++;
        }
        return true;
    }

    // Os atalhos da linguagem para a palavra digitada, os que começam por ela primeiro.
    public static List<SnippetEntry> Matching(string language, string word) =>
        For(language).Where(entry => Matches(entry.Prefix, word))
            .OrderBy(entry => entry.Prefix.StartsWith(word, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
