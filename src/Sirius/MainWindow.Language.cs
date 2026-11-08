using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Rendering;
using Sirius.Core;
using Sirius.Editing;
using Sirius.Lsp;
using Sirius.Theming;

namespace Sirius;

// Autocompletar, erros no código, ir para a definição e renomear símbolo.
public partial class MainWindow
{
    private const int MaxCompletionItems = 1000;
    private const double CompletionWidth = 280;
    private const int MaxHoverChars = 1500;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly LanguageService _language = new();
    private readonly ObservableCollection<ProblemRow> _problemRows = new();
    private CompletionWindow? _completion;
    private CancellationTokenSource? _completionCancel;
    private CancellationTokenSource? _hoverCancel;
    private ServerSpec? _missingServer;
    private bool _showingProblems;

    public LanguageService Language => _language;
    public IReadOnlyList<ProblemRow> ProblemRows => _problemRows;
    public bool IsShowingProblems => _showingProblems;
    public string ProblemsSummary => ProblemsButton.IsVisible ? ProblemsButton.Content as string ?? "" : "";
    public bool IsCompletionOpen => _completion is not null;
    public IEnumerable<string> CompletionLabels => _completion?.CompletionList.CompletionData.Select(item => item.Text) ?? [];
    public string? MissingServerName => _missingServer?.DisplayName;

    private void InitializeLanguage()
    {
        var area = CodeEditor.TextArea;
        ProblemsList.ItemsSource = _problemRows;
        area.TextEntering += OnPairTextEntering;
        area.TextEntered += OnTextEntered;
        area.TextEntered += OnTagTextEntered;
        area.Caret.PositionChanged += (_, _) => ForgetPairsOnOtherLine();
        area.TextView.BackgroundRenderers.Add(new DiagnosticRenderer(area.TextView, ActiveDiagnostics));
        area.TextView.PointerHover += OnEditorHover;
        area.TextView.PointerHoverStopped += OnEditorHoverStopped;
        area.AddHandler(PointerPressedEvent, OnEditorPointerPressed, RoutingStrategies.Tunnel);
        _language.DiagnosticsChanged += RefreshProblems;
        _language.ServerFailed += server => ShowStatus($"O servidor de {server.DisplayName} não está respondendo.");
    }

    private IReadOnlyList<LspDiagnostic> ActiveDiagnostics() =>
        ActiveDocument is { } document ? _language.DiagnosticsFor(document.FullPath) : [];

    // Problemas

    private void RefreshProblems()
    {
        var palette = Palette.Current;
        _problemRows.Clear();
        var all = _language.AllDiagnostics
            .OrderBy(item => item.Diagnostic.Severity)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Diagnostic.Line)
            .ToList();

        foreach (var (path, diagnostic) in all)
        {
            var brush = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => palette.TerminalError,
                DiagnosticSeverity.Warning => palette.TerminalWarning,
                _ => palette.CapsuleMuted,
            };
            var location = $"{Path.GetFileName(path)}:{diagnostic.Line + 1}";
            _problemRows.Add(new ProblemRow(FirstLine(diagnostic.Message), location, brush, path, diagnostic.Line, diagnostic.Character));
        }

        var errors = all.Count(item => item.Diagnostic.Severity == DiagnosticSeverity.Error);
        var warnings = all.Count(item => item.Diagnostic.Severity == DiagnosticSeverity.Warning);
        var others = all.Count - errors - warnings;
        var parts = new List<string>();
        if (errors > 0)
            parts.Add(errors == 1 ? "1 erro" : $"{errors} erros");
        if (warnings > 0)
            parts.Add(warnings == 1 ? "1 aviso" : $"{warnings} avisos");
        if (others > 0)
            parts.Add(others == 1 ? "1 dica" : $"{others} dicas");
        ProblemsButton.Content = string.Join(", ", parts);
        ProblemsButton.IsVisible = parts.Count > 0;
        RefreshUpdateNotice();

        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private void OnShowProblems(object? sender, RoutedEventArgs e) => ShowProblems();

    public void ShowProblems()
    {
        SetTerminalOpen(true);
        _showingProblems = true;
        HideVariables();
        TerminalHost.IsVisible = false;
        ProblemsList.IsVisible = true;
        TaskTabButton.IsVisible = _task is not null;
        TaskTabButton.Classes.Set("on", false);
        ShellTabButton.Classes.Set("on", false);
        ProblemsTabButton.Classes.Set("on", true);
        TerminalInfo.Text = "";
    }

    private void OnProblemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ProblemRow row)
            GoToLocation(row.FullPath, row.Line + 1, row.Character + 1);
    }

    public void OpenProblem(int index)
    {
        var row = _problemRows[index];
        GoToLocation(row.FullPath, row.Line + 1, row.Character + 1);
    }

    // Instalar o servidor que falta

    private void UpdateServerHint()
    {
        _missingServer = ActiveDocument is { } document ? _language.MissingServerFor(document.FullPath) : null;
        var canInstall = _missingServer is not null && LanguageServerCatalog.InstallCommand(_missingServer) is not null;
        InstallButton.IsVisible = canInstall;
        if (canInstall)
            InstallButton.Content = $"Instalar suporte a {_missingServer!.DisplayName}";
        // Chamado ao trocar de aba: a página do "Abrir site" acompanha o arquivo em uso.
        UpdateSiteButton();
    }

    private void OnInstallServer(object? sender, RoutedEventArgs e) => _ = InstallMissingServerAsync();

    public async Task InstallMissingServerAsync()
    {
        if (_missingServer is not { } server || LanguageServerCatalog.InstallCommand(server) is not { } command)
            return;
        if (IsTaskRunning)
        {
            ShowStatus("Pare o programa em execução antes de instalar.");
            return;
        }

        var choice = await AskAsync($"Instalar suporte a {server.DisplayName}?",
            $"Isto traz autocompletar e erros no código. O comando vai rodar no terminal:\n\n{command}", "Instalar", null);
        if (choice != DialogChoice.Primary)
            return;

        StartTask(command, code =>
        {
            if (code != 0)
                return;
            foreach (var installed in LanguageServerCatalog.InstalledTogetherWith(server))
                _language.Retry(_documents, installed);
            UpdateServerHint();
            ShowStatus($"Suporte a {server.DisplayName} instalado");
        });
    }

    // Autocompletar

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private static int WordStart(AvaloniaEdit.Document.TextDocument document, int offset)
    {
        while (offset > 0 && IsWordChar(document.GetCharAt(offset - 1)))
            offset--;
        return offset;
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
            SuggestAfterTyping(e.Text[^1]);
    }

    private void SuggestAfterTyping(char typed)
    {
        if (ActiveDocument is not { } document)
            return;
        // Uma lista só com o Emmet é refeita a cada tecla, para mostrar a abreviação como ela está agora.
        if (_emmetOnlyCompletion)
            CloseCompletion();
        if (_completion is not null)
            return;

        var client = _language.ClientForDocument(document);
        var wantsServer = client is { SupportsCompletion: true } && ((IsWordChar(typed) && !char.IsDigit(typed)) || client.CompletionTriggers.Contains(typed));
        if (wantsServer || EmmetSuggestion(document) is not null)
            _ = RequestCompletionAsync();
    }

    // Lista aberta só com a sugestão do Emmet, sem nada do servidor.
    private bool _emmetOnlyCompletion;

    public async Task RequestCompletionAsync()
    {
        if (ActiveDocument is not { } document)
            return;

        var area = CodeEditor.TextArea;
        var requestedAt = area.Caret.Offset;
        var (line, character) = LspPositions.FromOffset(document.Document, requestedAt);

        // Sem servidor, ou sem resposta dele, ainda pode haver a sugestão do Emmet.
        List<CompletionEntry> entries = [];
        if (_language.ClientForDocument(document) is { SupportsCompletion: true } client)
        {
            _completionCancel?.Cancel();
            using var cancel = new CancellationTokenSource(RequestTimeout);
            _completionCancel = cancel;
            try
            {
                entries = await client.CompletionAsync(document.FullPath, line, character, cancel.Token);
            }
            catch (Exception e) when (e is LspException or OperationCanceledException)
            {
                entries = [];
            }
            finally
            {
                if (_completionCancel == cancel)
                    _completionCancel = null;
            }
        }

        // A pessoa pode ter continuado digitando, trocado de aba ou saído da palavra enquanto esperava.
        var caret = area.Caret.Offset;
        if (_completion is not null || ActiveDocument != document
            || caret < requestedAt || document.Document.GetLineByOffset(caret).LineNumber != line + 1)
            return;

        var emmet = EmmetSuggestion(document);
        if (entries.Count == 0 && emmet is null)
            return;
        ShowCompletion(document, entries, caret, emmet);
    }

    private void ShowCompletion(OpenDocument document, List<CompletionEntry> entries, int caret, ICompletionData? emmet = null)
    {
        var text = document.Document;
        var caretLine = text.GetLineByOffset(caret);
        int? EditStart(CompletionEntry entry) =>
            entry.Edit is { } edit && edit.StartLine == caretLine.LineNumber - 1 && caretLine.Offset + edit.StartCharacter <= caret
                ? caretLine.Offset + edit.StartCharacter
                : null;

        // O filtro da lista começa onde o servidor disse que a palavra começa, se ele disse. Só com o Emmet,
        // não há o que filtrar: a lista é refeita a cada tecla.
        var emmetOnly = entries.Count == 0 && emmet is not null;
        var start = emmetOnly ? caret : entries.Select(EditStart).FirstOrDefault(offset => offset is not null) ?? WordStart(text, caret);
        var window = new CompletionWindow(CodeEditor.TextArea)
        {
            StartOffset = start,
            EndOffset = caret,
            CloseWhenCaretAtBeginning = true,
            // O padrão cortava nomes longos, como background-attachment.
            Width = CompletionWidth,
        };

        // Filtra pelo que já foi digitado antes de cortar: o CSS tem centenas de propriedades,
        // e cortar primeiro faria z-index nunca aparecer, mesmo digitando "z".
        var typed = text.GetText(start, caret - start);
        var matching = entries.Where(entry => entry.Label.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = matching.Count > 0 ? matching : entries;

        var items = window.CompletionList.CompletionData;
        // A abreviação vem primeiro, como no VS Code.
        if (emmet is not null)
            items.Add(emmet);
        foreach (var entry in candidates.OrderBy(e => e.SortText ?? e.Label, StringComparer.OrdinalIgnoreCase).Take(MaxCompletionItems))
        {
            // O que o servidor quer trocar depois do cursor, como o resto de uma palavra.
            var after = entry.Edit is { } edit && edit.EndLine == caretLine.LineNumber - 1
                ? Math.Max(0, Math.Min(caretLine.EndOffset, caretLine.Offset + edit.EndCharacter) - caret)
                : 0;
            items.Add(new CompletionItem(entry, EditStart(entry), after, SuggestAgainSoon, InsertSnippet));
        }

        window.Closed += (_, _) =>
        {
            if (_completion != window)
                return;
            _completion = null;
            _emmetOnlyCompletion = false;
        };
        _completion = window;
        _emmetOnlyCompletion = emmetOnly;
        window.Show();
        if (emmetOnly)
            window.CompletionList.SelectedItem = emmet;
        else
            window.CompletionList.SelectItem(document.Document.GetText(start, caret - start));
    }

    // Depois que a lista atual fechar: só cabe uma aberta por vez.
    private void SuggestAgainSoon() => Dispatcher.UIThread.Post(() => _ = RequestCompletionAsync());

    public void SetCaretOffset(int offset) => CodeEditor.TextArea.Caret.Offset = offset;

    // Usado pelos testes, para escolher como quem aperta Enter na lista.
    public bool AcceptCompletion(string label)
    {
        if (_completion is not { } window)
            return false;
        var item = window.CompletionList.CompletionData.FirstOrDefault(data => data.Text == label);
        if (item is null)
            return false;
        window.CompletionList.SelectedItem = item;
        window.CompletionList.RequestInsertion(EventArgs.Empty);
        return true;
    }

    public void CloseCompletion()
    {
        _completionCancel?.Cancel();
        _completion?.Close();
        _completion = null;
        _emmetOnlyCompletion = false;
    }

    // Ir para a definição

    public async Task GoToDefinitionAsync(int? offset = null)
    {
        if (ActiveDocument is not { } document)
            return;
        if (_language.ClientForDocument(document) is not { SupportsDefinition: true } client)
        {
            ShowStatus("Ir para a definição não está disponível neste arquivo.");
            return;
        }

        var (line, character) = LspPositions.FromOffset(document.Document, offset ?? CodeEditor.TextArea.Caret.Offset);
        using var cancel = new CancellationTokenSource(RequestTimeout);
        try
        {
            if (await client.DefinitionAsync(document.FullPath, line, character, cancel.Token) is { } location)
                GoToLocation(location.Path, location.Line + 1, location.Character + 1);
            else
                ShowStatus("Nenhuma definição aqui. Ponha o cursor no nome de uma função.");
        }
        catch (Exception e) when (e is LspException or OperationCanceledException)
        {
            ShowStatus("O servidor não respondeu a tempo.");
        }
    }

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (OffsetAt(e) is { } offset)
        {
            e.Handled = true;
            _ = GoToDefinitionAsync(offset);
        }
    }

    private int? OffsetAt(PointerEventArgs e) => OffsetAt(e.GetPosition(CodeEditor.TextArea.TextView));

    // O caractere sob o mouse. GetPosition arredonda para a emenda mais próxima, e assim a metade
    // direita da última letra contava como fora da palavra: em $n, é um bom pedaço do alvo.
    public int? OffsetAt(Point point)
    {
        var view = CodeEditor.TextArea.TextView;
        if (ActiveDocument is not { } document || view.GetPositionFloor(point + view.ScrollOffset) is not { } position)
            return null;
        return document.Document.GetOffset(position.Location);
    }

    // Dica ao parar o mouse

    private async void OnEditorHover(object? sender, PointerEventArgs e)
    {
        if (ActiveDocument is not { } document || OffsetAt(e) is not { } offset)
            return;

        _hoverCancel?.Cancel();
        var cancel = new CancellationTokenSource(RequestTimeout);
        _hoverCancel = cancel;

        // Durante uma parada, o valor é o que a pessoa quer ver, e ele já está aqui, sem perguntar a ninguém.
        if (DebugTipAt(offset) is { } debugTip)
        {
            ShowEditorTip(debugTip);
            return;
        }

        var parts = _language.DiagnosticsFor(document.FullPath)
            .Where(diagnostic =>
            {
                var (start, end) = DiagnosticRenderer.Span(document.Document, diagnostic);
                return offset >= start && offset <= end;
            })
            .Select(diagnostic => diagnostic.Message)
            .ToList();

        if (_language.ClientForDocument(document) is { SupportsHover: true } client)
        {
            var (line, character) = LspPositions.FromOffset(document.Document, offset);
            try
            {
                if (await client.HoverAsync(document.FullPath, line, character, cancel.Token) is { } text)
                    parts.Add(text);
            }
            catch (Exception ex) when (ex is LspException or OperationCanceledException)
            {
                // Sem dica do servidor, ainda mostramos o erro se houver.
            }
        }

        if (cancel.IsCancellationRequested || parts.Count == 0)
            return;
        ShowEditorTip(string.Join("\n\n", parts));
    }

    private void ShowEditorTip(string tip)
    {
        ToolTip.SetTip(CodeEditor, new TextBlock
        {
            Text = tip.Length > MaxHoverChars ? tip[..MaxHoverChars] + "…" : tip,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
        });
        ToolTip.SetIsOpen(CodeEditor, true);
    }

    private void OnEditorHoverStopped(object? sender, PointerEventArgs e)
    {
        _hoverCancel?.Cancel();
        ToolTip.SetIsOpen(CodeEditor, false);
    }

    // Renomear símbolo

    public async Task RenameSymbolAsync()
    {
        if (ActiveDocument is not { } document)
            return;
        if (_language.ClientForDocument(document) is not { SupportsRename: true } client)
        {
            ShowStatus("O servidor desta linguagem não oferece renomear símbolo.");
            return;
        }

        var text = document.Document;
        var caret = CodeEditor.TextArea.Caret.Offset;
        var start = WordStart(text, caret);
        var end = caret;
        while (end < text.TextLength && IsWordChar(text.GetCharAt(end)))
            end++;
        var word = text.GetText(start, end - start);
        if (word.Length == 0)
        {
            ShowStatus("Coloque o cursor sobre um nome para renomear.");
            return;
        }

        var name = await AskTextAsync($"Renomear {word}", "Todas as referências no projeto serão atualizadas.", "Renomear",
            value => string.IsNullOrWhiteSpace(value) ? "Digite um nome." : null, word);
        if (name is null || name == word)
            return;

        var (line, character) = LspPositions.FromOffset(text, caret);
        using var cancel = new CancellationTokenSource(RequestTimeout);
        try
        {
            ApplyWorkspaceEdit(await client.RenameAsync(document.FullPath, line, character, name, cancel.Token), document);
        }
        catch (Exception e) when (e is LspException or OperationCanceledException)
        {
            ShowStatus("Não foi possível renomear este símbolo.");
        }
    }

    // Encontrar todos os usos

    public async Task FindUsesAsync()
    {
        if (ActiveDocument is not { IsImage: false } document)
            return;
        if (_language.ClientForDocument(document) is not { SupportsReferences: true } client)
        {
            ShowStatus("Encontrar os usos não está disponível neste arquivo.");
            return;
        }

        var text = document.Document;
        var caret = CodeEditor.TextArea.Caret.Offset;
        var start = WordStart(text, caret);
        var end = caret;
        while (end < text.TextLength && IsWordChar(text.GetCharAt(end)))
            end++;
        var word = text.GetText(start, end - start);
        if (word.Length == 0)
        {
            ShowStatus("Ponha o cursor no nome de uma função ou variável.");
            return;
        }

        // Do começo do nome: logo depois dele, alguns servidores já olham para o que vem a seguir.
        var (line, character) = LspPositions.FromOffset(text, start);
        List<LspReference> references;
        using (var cancel = new CancellationTokenSource(RequestTimeout))
        {
            try
            {
                references = await client.ReferencesAsync(document.FullPath, line, character, cancel.Token);
            }
            catch (Exception e) when (e is LspException or OperationCanceledException)
            {
                ShowStatus("O servidor não respondeu a tempo.");
                return;
            }
        }
        if (references.Count == 0)
        {
            ShowStatus($"Nenhum uso de {word} encontrado.");
            return;
        }
        ShowUses(word, document.FullPath, references);
    }

    // O nome cujos usos estão no painel. O aviso de texto mudado chega depois, e não pode trocá-los pela busca de texto.
    private string? _usesQuery;

    private void OnSearchTextChanged()
    {
        if (_usesQuery is not null && SearchInput.Text == _usesQuery)
            return;
        _usesQuery = null;
        ReplaceBar.IsVisible = true;
        UsesHint.IsVisible = false;
        ScheduleSearch();
    }

    public bool IsReplaceVisible => ReplaceBar.IsVisible;

    // No painel da busca, arquivo por arquivo, com o arquivo aberto primeiro.
    private void ShowUses(string word, string currentPath, List<LspReference> references)
    {
        var hits = new List<SearchHit>();
        var files = 0;
        foreach (var group in references
                     .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => string.Equals(g.Key, currentPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            // O texto da aba, se estiver aberta, porque pode ter mudanças ainda não salvas.
            var content = _documents.Find(d => string.Equals(d.FullPath, group.Key, StringComparison.OrdinalIgnoreCase))?.Document.Text
                ?? ProjectSearch.LoadOrNull(group.Key)?.Text;
            if (content is null)
                continue;
            var spots = group.Select(r => (r.Line, r.Character, r.EndLine == r.Line ? r.EndCharacter - r.Character : int.MaxValue));
            var found = ProjectSearch.HitsAt(group.Key, content, spots);
            if (found.Count == 0)
                continue;
            hits.AddRange(found);
            files++;
        }

        OpenSearch();
        // O texto da busca mostra o nome, sem disparar uma busca de texto por cima destes resultados.
        _usesQuery = word;
        SearchInput.Text = word;
        _searchDelay.Stop();
        _searchCancel?.Cancel();
        ShowSearchResult(new SearchResult(hits, files, false, false));
        SearchStatus.Text = $"{(hits.Count == 1 ? "1 uso" : $"{hits.Count} usos")} de {word} em {(files == 1 ? "1 arquivo" : $"{files} arquivos")}";
        // Substituir trocaria todo texto igual, inclusive o que não está na lista. Para o nome, o certo é renomear.
        ReplaceBar.IsVisible = false;
        UsesHint.IsVisible = true;
    }

    private void ApplyWorkspaceEdit(Dictionary<string, List<LspTextEdit>> edits, OpenDocument original)
    {
        var changed = 0;
        foreach (var (path, list) in edits)
        {
            // Arquivos fora das abas abrem em aba nova e ficam alterados, para você revisar antes de salvar.
            OpenFile(path);
            if (!string.Equals(ActiveDocument?.FullPath, path, StringComparison.OrdinalIgnoreCase))
                continue;
            LspPositions.ApplyEdits(ActiveDocument!.Document, list);
            changed++;
        }
        Activate(original);
        ShowStatus(changed == 1 ? "Renomeado em 1 arquivo" : $"Renomeado em {changed} arquivos");
    }
}
