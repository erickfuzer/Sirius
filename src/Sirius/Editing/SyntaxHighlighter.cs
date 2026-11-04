using AvaloniaEdit.Document;
using Sirius.Core;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace Sirius.Editing;

// Lê o documento linha a linha com a gramática da linguagem. Só analisa o que a tela pede
// e, depois de uma edição, refaz apenas a partir da linha alterada.
public sealed class SyntaxHighlighter
{
    private const int MaxDocumentChars = 5_000_000;
    private const int MaxLineChars = 20_000;
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(1);

    private readonly record struct LineResult(SyntaxToken[] Tokens, IStateStack? EndState);

    private readonly IGrammar _grammar;
    private readonly TextDocument _document;
    private readonly List<LineResult> _lines = new();

    private SyntaxHighlighter(IGrammar grammar, TextDocument document)
    {
        _grammar = grammar;
        _document = document;
        _document.Changed += OnDocumentChanged;
    }

    public static SyntaxHighlighter? ForDocument(string path, TextDocument document)
    {
        if (document.TextLength > MaxDocumentChars)
            return null;
        var grammar = GrammarCatalog.ForPath(path);
        return grammar is null ? null : new SyntaxHighlighter(grammar, document);
    }

    public void Detach() => _document.Changed -= OnDocumentChanged;

    public IReadOnlyList<SyntaxToken> TokensFor(int lineNumber)
    {
        for (var n = _lines.Count + 1; n <= lineNumber; n++)
            _lines.Add(Tokenize(n, n > 1 ? _lines[n - 2].EndState : null));
        return _lines[lineNumber - 1].Tokens;
    }

    private LineResult Tokenize(int lineNumber, IStateStack? previous)
    {
        var line = _document.GetLineByNumber(lineNumber);
        if (line.Length > MaxLineChars)
            return new LineResult([], previous);

        var text = _document.GetText(line);
        var result = _grammar.TokenizeLine(text, previous, LineTimeout);
        var tokens = new List<SyntaxToken>(result.Tokens.Length);
        foreach (var token in result.Tokens)
        {
            // A gramática conta uma quebra de linha invisível depois do último caractere.
            var end = Math.Min(token.EndIndex, line.Length);
            if (end <= token.StartIndex)
                continue;
            var kind = SyntaxScopes.Classify(token.Scopes, text[token.StartIndex]);
            if (kind is not null)
                tokens.Add(new SyntaxToken(token.StartIndex, end - token.StartIndex, kind.Value));
        }
        return new LineResult(tokens.ToArray(), result.RuleStack);
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        var first = _document.GetLineByOffset(e.Offset).LineNumber - 1;
        if (first < _lines.Count)
            _lines.RemoveRange(first, _lines.Count - first);
    }
}

internal static class GrammarCatalog
{
    private const string PhpCodeScope = "source.php";
    private const string PhpPageScope = "text.html.php";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [".axaml"] = ".xml", [".xaml"] = ".xml", [".csproj"] = ".xml", [".slnx"] = ".xml",
        [".props"] = ".xml", [".targets"] = ".xml", [".manifest"] = ".xml",
    };

    private static readonly RegistryOptions Options = new(ThemeName.DarkPlus);
    private static readonly Registry GrammarRegistry = new(Options);
    private static readonly Dictionary<string, IGrammar?> Cache = new(StringComparer.Ordinal);

    public static IGrammar? ForPath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension.Length == 0)
            return null;

        var scope = Options.GetScopeByExtension(Aliases.GetValueOrDefault(extension, extension));
        if (scope is null)
            return null;

        // source.php lê o arquivo inteiro como código, e aí um #fff no CSS vira comentário.
        // Um .php é HTML com trechos de PHP, e é isso que a outra gramática entende.
        if (scope == PhpCodeScope)
            scope = PhpPageScope;

        if (!Cache.TryGetValue(scope, out var grammar))
        {
            grammar = GrammarRegistry.LoadGrammar(scope);
            Cache[scope] = grammar;
        }
        return grammar;
    }
}
