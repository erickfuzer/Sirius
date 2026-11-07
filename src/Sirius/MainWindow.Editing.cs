using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Sirius.Core;
using Sirius.Lsp;

namespace Sirius;

// Comentar, duplicar e mover linhas, ir para uma linha, quebra de linha, formatar e o menu do botão direito.
public partial class MainWindow
{
    private static readonly TimeSpan FormatTimeout = TimeSpan.FromSeconds(20);

    public bool IsWordWrapOn => CodeEditor.WordWrap;

    private void InitializeEditing()
    {
        CodeEditor.WordWrap = _settings.WordWrap;
        CodeEditor.TextArea.AddHandler(PointerPressedEvent, OnEditorRightPressed, RoutingStrategies.Tunnel);
        CodeEditor.ContextMenu = BuildEditorMenu();
        CaretPosition.PointerPressed += OnCaretPositionPressed;
    }

    private bool CanEditText => ActiveDocument is { IsImage: false } && !CodeEditor.IsReadOnly;

    // Comentários

    public void ToggleComment()
    {
        if (!CanEditText || ActiveDocument is not { } active)
            return;
        var document = active.Document;
        var (first, last) = SelectedLines();
        var at = LineEditing.FirstVisibleOffset(document, first);
        if (LineEditing.CommentStyleAt(active.FullPath, document.Text, at) is not { } style)
        {
            ShowStatus("Este tipo de arquivo não tem comentários.");
            return;
        }
        LineEditing.ToggleComment(document, first, last, style);
    }

    // Duplicar e mover

    public void DuplicateLines(bool keepCaretAbove = false)
    {
        if (!CanEditText || ActiveDocument is not { } active)
            return;
        var (first, last) = SelectedLines();
        var place = SelectionPlace();
        var count = LineEditing.Duplicate(active.Document, first, last);
        RestorePlace(place, keepCaretAbove ? 0 : count);
    }

    public void MoveLines(bool up)
    {
        if (!CanEditText || ActiveDocument is not { } active)
            return;
        var (first, last) = SelectedLines();
        var place = SelectionPlace();
        if (LineEditing.Move(active.Document, first, last, up))
            RestorePlace(place, up ? -1 : 1);
    }

    private (int First, int Last) SelectedLines()
    {
        var area = CodeEditor.TextArea;
        if (area.Selection.IsEmpty)
            return (area.Caret.Line, area.Caret.Line);
        var segment = area.Selection.SurroundingSegment;
        return LineEditing.LinesOf(area.Document, segment.Offset, segment.EndOffset);
    }

    private (TextLocation Caret, TextLocation? Start, TextLocation? End) SelectionPlace()
    {
        var area = CodeEditor.TextArea;
        return area.Selection.IsEmpty
            ? (area.Caret.Location, null, null)
            : (area.Caret.Location, area.Selection.StartPosition.Location, area.Selection.EndPosition.Location);
    }

    // Leva o cursor e a seleção junto com as linhas, na mesma coluna.
    private void RestorePlace((TextLocation Caret, TextLocation? Start, TextLocation? End) place, int lines)
    {
        var area = CodeEditor.TextArea;
        TextLocation Shift(TextLocation location) => new(location.Line + lines, location.Column);
        if (place.Start is { } start && place.End is { } end)
            area.Selection = Selection.Create(area, area.Document.GetOffset(Shift(start)), area.Document.GetOffset(Shift(end)));
        else
            area.ClearSelection();
        area.Caret.Location = Shift(place.Caret);
        area.Caret.BringCaretToView();
    }

    // Ir para uma linha

    // A barra de comando entende ":42" como a linha 42 do arquivo aberto.
    private List<(string Name, string Hint, Action Run)>? LineCommands(string query)
    {
        if (!query.StartsWith(':') || ActiveDocument is not { IsImage: false } active)
            return null;
        var count = active.Document.LineCount;
        var number = query[1..].Trim();
        if (int.TryParse(number, out var line) && line > 0)
        {
            var target = Math.Min(line, count);
            return [($"Ir para a linha {target}", $"de {count}", () => GoToLine(target))];
        }
        return [("Digite o número da linha", $"de 1 a {count}", () => OpenPalette(":"))];
    }

    public void OpenGoToLine()
    {
        if (ActiveDocument is { IsImage: false })
            OpenPalette(":");
    }

    public void GoToLine(int line)
    {
        if (ActiveDocument is not { IsImage: false } active)
            return;
        var document = active.Document;
        var number = Math.Clamp(line, 1, document.LineCount);
        var area = CodeEditor.TextArea;
        area.ClearSelection();
        area.Caret.Offset = LineEditing.FirstVisibleOffset(document, number);
        Dispatcher.UIThread.Post(() => CodeEditor.ScrollToLine(number), DispatcherPriority.Loaded);
        FocusActiveDocument();
    }

    private void OnCaretPositionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(CaretPosition).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        OpenGoToLine();
    }

    // Quebra de linha

    public void ToggleWordWrap()
    {
        _settings.WordWrap = !_settings.WordWrap;
        _settings.Save(_settingsPath);
        CodeEditor.WordWrap = _settings.WordWrap;
        ShowStatus(_settings.WordWrap ? "Quebra de linha ligada." : "Quebra de linha desligada.");
    }

    // Formatar

    public async Task FormatDocumentAsync()
    {
        if (!CanEditText || ActiveDocument is not { } document)
            return;
        if (_language.ClientForDocument(document) is not { SupportsFormatting: true } client)
        {
            ShowStatus("Formatar não está disponível neste arquivo.");
            return;
        }

        var before = document.Document.Text;
        using var cancel = new CancellationTokenSource(FormatTimeout);
        try
        {
            var options = CodeEditor.Options;
            var edits = await client.FormatAsync(document.FullPath, options.IndentationSize, options.ConvertTabsToSpaces, cancel.Token);
            // A pessoa pode ter digitado ou trocado de aba enquanto o servidor pensava.
            if (ActiveDocument != document || document.Document.Text != before)
                return;
            if (edits.Count > 0)
                LspPositions.ApplyEdits(document.Document, edits);
            ShowStatus(document.Document.Text == before ? "O arquivo já estava formatado." : "Arquivo formatado.");
        }
        catch (Exception e) when (e is LspException or OperationCanceledException)
        {
            ShowStatus("O servidor não conseguiu formatar este arquivo.");
        }
    }

    // Menu do botão direito

    private ContextMenu BuildEditorMenu()
    {
        MenuItem Item(string header, Action run, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null)
                item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => run();
            return item;
        }

        var cut = Item("Recortar", () => CodeEditor.Cut(), "Ctrl+X");
        var copy = Item("Copiar", () => CodeEditor.Copy(), "Ctrl+C");
        var paste = Item("Colar", () => CodeEditor.Paste(), "Ctrl+V");
        var editing = new List<MenuItem>
        {
            Item("Comentar linhas", ToggleComment),
            Item("Duplicar linha", () => DuplicateLines()),
            Item("Mover para cima", () => MoveLines(up: true)),
            Item("Mover para baixo", () => MoveLines(up: false)),
            Item("Formatar arquivo", () => _ = FormatDocumentAsync()),
        };
        var definition = Item("Ir para a definição", () => _ = GoToDefinitionAsync(), "F12");
        var rename = Item("Renomear símbolo", () => _ = RenameSymbolAsync(), "F2");
        var goToLine = Item("Ir para a linha", OpenGoToLine, "Ctrl+G");

        var menu = new ContextMenu();
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        foreach (var item in editing)
            menu.Items.Add(item);
        menu.Items.Add(new Separator());
        menu.Items.Add(definition);
        menu.Items.Add(rename);
        menu.Items.Add(goToLine);

        menu.Opening += (_, e) =>
        {
            if (ActiveDocument is not { IsImage: false })
            {
                e.Cancel = true;
                return;
            }
            var hasSelection = !CodeEditor.TextArea.Selection.IsEmpty;
            cut.IsEnabled = hasSelection && CanEditText;
            copy.IsEnabled = hasSelection;
            paste.IsEnabled = CanEditText;
            foreach (var item in editing)
                item.IsEnabled = CanEditText;
        };
        return menu;
    }

    // O botão direito fora da seleção põe o cursor onde clicou, para o menu agir ali.
    private void OnEditorRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(CodeEditor.TextArea).Properties.IsRightButtonPressed || OffsetAt(e) is not { } offset)
            return;
        var area = CodeEditor.TextArea;
        if (!area.Selection.IsEmpty && area.Selection.SurroundingSegment is { } segment
            && offset >= segment.Offset && offset <= segment.EndOffset)
            return;
        area.ClearSelection();
        area.Caret.Offset = offset;
    }

    // Teclas de edição com Alt, que no Windows não chegam com o Ctrl.
    private bool HandleAltKey(KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        if (!modifiers.HasFlag(KeyModifiers.Alt) || modifiers.HasFlag(KeyModifiers.Control) || TerminalHost.IsFocused)
            return false;
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.Z && !shift)
        {
            ToggleWordWrap();
            return true;
        }
        if (!CodeEditor.TextArea.IsKeyboardFocusWithin)
            return false;

        switch (e.Key)
        {
            case Key.Up when shift:
                DuplicateLines(keepCaretAbove: true);
                return true;
            case Key.Down when shift:
                DuplicateLines();
                return true;
            case Key.Up:
                MoveLines(up: true);
                return true;
            case Key.Down:
                MoveLines(up: false);
                return true;
            case Key.F when shift:
                _ = FormatDocumentAsync();
                return true;
        }
        return false;
    }
}
