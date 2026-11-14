using Sirius.Editing;
using Sirius.Lsp;

namespace Sirius;

// "Ir para…" uma função, classe, seletor do CSS ou tag do HTML do arquivo aberto, como o Ctrl Shift O do
// VS Code. É a barra de comando com "@" no começo; a lista vem do servidor da linguagem.
public partial class MainWindow
{
    private List<LspSymbol>? _symbols;
    private OpenDocument? _symbolsDocument;
    private object? _symbolsVersion;
    private string? _symbolsMessage;
    private bool _symbolsLoading;

    public bool CanListSymbols => ActiveDocument is { IsImage: false };

    public void OpenSymbols()
    {
        if (CanListSymbols)
            OpenPalette("@");
    }

    private List<(string Name, string Hint, Action Run)>? SymbolCommands(string query)
    {
        if (!query.StartsWith('@') || ActiveDocument is not { IsImage: false } active)
            return null;

        var version = active.Document.Version;
        if (!ReferenceEquals(_symbolsDocument, active) || !Equals(_symbolsVersion, version))
        {
            _symbols = null;
            _symbolsMessage = null;
            _symbolsDocument = active;
            _symbolsVersion = version;
            _ = LoadSymbolsAsync(active, version);
        }

        // Mensagens não fecham a barra: o clique volta ao "@".
        if (_symbols is null)
            return [(_symbolsMessage ?? "Procurando funções e seletores…", "", OpenSymbols)];

        var filter = query[1..].Trim();
        var path = active.FullPath;
        var commands = _symbols
            .Where(symbol => filter.Length == 0 || symbol.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(symbol => (
                Name: (filter.Length == 0 ? new string(' ', symbol.Depth * 3) : "") + symbol.Name,
                Hint: $"{KindName(path, symbol)} · linha {symbol.Line + 1}",
                Run: (Action)(() => GoToLocation(path, symbol.Line + 1, symbol.Character + 1))))
            .ToList();
        if (commands.Count == 0)
            return [(_symbols.Count == 0 ? "Nenhuma função ou seletor neste arquivo" : $"Nada com \"{filter}\"", "", () => OpenPalette("@"))];
        return commands;
    }

    private async Task LoadSymbolsAsync(OpenDocument document, object version)
    {
        var client = _language.ClientForDocument(document);
        if (client is null && _language.MissingServerFor(document.FullPath) is null && LanguageServerCatalog.ForPath(document.FullPath) is not null)
        {
            // O servidor ainda está abrindo: espera um pouco e pede de novo, sem guardar a resposta vazia.
            _symbolsMessage = "Esperando o servidor da linguagem abrir…";
            _symbolsVersion = null;
            Avalonia.Threading.DispatcherTimer.RunOnce(RefreshSymbolsInPalette, TimeSpan.FromMilliseconds(400));
            RefreshSymbolsInPalette();
            return;
        }
        if (client is not { SupportsDocumentSymbols: true })
        {
            _symbolsMessage = _language.MissingServerFor(document.FullPath) is { } missing
                ? $"Instale o suporte a {missing.DisplayName} pela cápsula para ver a lista"
                : "Este tipo de arquivo não tem lista de funções";
            RefreshSymbolsInPalette();
            return;
        }

        _symbolsLoading = true;
        List<LspSymbol>? symbols = null;
        try
        {
            using var cancel = new CancellationTokenSource(RequestTimeout);
            symbols = await client.DocumentSymbolsAsync(document.FullPath, cancel.Token);
        }
        catch (Exception e) when (e is LspException or OperationCanceledException)
        {
            _symbolsMessage = "O servidor não respondeu a tempo";
        }
        finally
        {
            _symbolsLoading = false;
        }

        // A lista pode chegar depois de outra tecla ou de outra aba: só vale se ainda é o mesmo texto.
        if (!ReferenceEquals(_symbolsDocument, document) || !Equals(_symbolsVersion, version))
            return;
        if (symbols is not null)
            _symbols = Visible(symbols);
        RefreshSymbolsInPalette();
    }

    public bool IsLoadingSymbols => _symbolsLoading;

    // As variáveis de dentro das funções, como os parâmetros, enchem a lista sem ajudar a navegar.
    private static List<LspSymbol> Visible(List<LspSymbol> symbols) =>
        symbols.Where(symbol => !(symbol.Kind == 13 && symbol.Depth > 0)).ToList();

    private void RefreshSymbolsInPalette()
    {
        if (IsPaletteOpen && (PaletteInput.Text ?? "").TrimStart().StartsWith('@'))
            RefreshPalette();
    }

    // O nome de cada tipo como se fala no curso: no CSS, "seletor"; no HTML, "tag".
    public static string KindName(string path, LspSymbol symbol)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var css = extension is ".css" or ".scss" or ".sass" or ".less";
        var markup = extension is ".html" or ".htm" or ".php" or ".vue" or ".svelte";
        if (css || (markup && symbol.Kind == 5 && symbol.Container is null && !symbol.Name.Contains('$')))
        {
            if (symbol.Name.StartsWith('@'))
                return "regra";
            if (symbol.Name.StartsWith("--", StringComparison.Ordinal) || symbol.Kind == 13)
                return "variável";
            if (css || IsCssSelector(symbol.Name))
                return "seletor";
        }
        return symbol.Kind switch
        {
            2 => "módulo",
            3 => "namespace",
            5 => "classe",
            6 => "método",
            7 => "propriedade",
            8 => markup ? "tag" : "campo",
            9 => "construtor",
            10 => "enum",
            11 => "interface",
            12 => "função",
            13 => "variável",
            14 => "constante",
            22 => "item",
            23 => "estrutura",
            26 => "tipo",
            _ => "nome",
        };
    }

    // Um h1 dentro do <style> de uma página vem como classe; o nome de uma classe do PHP não tem espaço nem ponto antes.
    private static bool IsCssSelector(string name) =>
        name.IndexOfAny([' ', '.', '#', ':', '>', ',', '[', '*']) >= 0 || name.All(c => char.IsLower(c) || char.IsDigit(c));
}
