using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius;

// Trechos alterados desde o último commit: a faixa colorida da margem mostra como era antes, e dá para desfazer
// só aquele trecho, ou o que estiver selecionado, sem perder o resto.
public partial class MainWindow
{
    private const int ChangePeekMaxLines = 12;

    private readonly TypographicColorizer _changePeekColors = new();
    private readonly List<DiffRowKind> _changePeekKinds = new();
    private List<ChangeBlock> _changeBlocks = new();
    private int? _changePeekIndex;

    public IReadOnlyList<ChangeBlock> ChangeBlockList => _changeBlocks;
    public bool IsChangePeekOpen => ChangePeek.IsVisible;
    public string ChangePeekOldText => ChangePeekEditor.IsVisible ? ChangePeekEditor.Document?.Text ?? "" : "";
    public string ChangePeekTitleText => ChangePeekTitle.Text ?? "";
    public string ChangePeekNoteText => ChangePeekNote.IsVisible ? ChangePeekNote.Text ?? "" : "";
    public Thickness ChangePeekMargin => ChangePeek.Margin;

    private void InitializeChangePeek()
    {
        _gutter.ChangeMarkClicked += line => ShowChangeAt(line);
        ChangePeekEditor.TextArea.TextView.LineTransformers.Add(_changePeekColors);
        ChangePeekEditor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackground(() => _changePeekKinds, false));
        ChangePeekEditor.Options.EnableHyperlinks = false;
        CodeEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => PlaceChangePeek();
        CodeEditor.TextArea.TextView.VisualLinesChanged += (_, _) => PlaceChangePeek();
    }

    // O texto do último commit de onde os trechos saíram, para colorir as linhas antigas como lá.
    private string? _changeBaseText;

    private void SetChangeBlocks(List<ChangeBlock>? blocks, string? baseText = null)
    {
        _changeBlocks = blocks ?? new List<ChangeBlock>();
        _changeBaseText = blocks is null ? null : baseText;
        if (_changePeekIndex is { } index && index >= _changeBlocks.Count)
            CloseChangePeek();
    }

    // Mostra o trecho da marca nesta linha. Devolve se havia um.
    public bool ShowChangeAt(int line)
    {
        var index = ChangeBlocks.IndexAt(_changeBlocks, line);
        if (index < 0)
            return false;
        ShowChange(index);
        return true;
    }

    // Pela barra de comando: o trecho do cursor ou, sem um ali, o próximo abaixo dele.
    public void ShowChangeNearCaret()
    {
        if (_changeBlocks.Count == 0)
        {
            ShowStatus("Nada mudou neste arquivo desde o último commit.");
            return;
        }
        var line = CodeEditor.TextArea.Caret.Line;
        if (!ShowChangeAt(line))
            ShowChange(NextChangeIndex(line, 1));
    }

    public void MoveToChange(int direction)
    {
        if (_changeBlocks.Count == 0)
        {
            ShowStatus("Nada mudou neste arquivo desde o último commit.");
            return;
        }
        if (_changePeekIndex is { } index)
            ShowChange((index + direction + _changeBlocks.Count) % _changeBlocks.Count);
        else
            ShowChange(NextChangeIndex(CodeEditor.TextArea.Caret.Line, direction));
    }

    // O primeiro trecho depois da linha, ou antes dela, dando a volta no arquivo como no VS Code.
    private int NextChangeIndex(int line, int direction)
    {
        if (direction > 0)
        {
            var next = _changeBlocks.FindIndex(block => block.First > line || (block.IsDeleted && block.MarkLine > line));
            return next >= 0 ? next : 0;
        }
        var previous = _changeBlocks.FindLastIndex(block => block.Last < line);
        return previous >= 0 ? previous : _changeBlocks.Count - 1;
    }

    private void ShowChange(int index)
    {
        if (ActiveDocument is not { IsImage: false } document || index < 0 || index >= _changeBlocks.Count)
            return;
        CloseColorPicker();
        _changePeekIndex = index;
        var block = _changeBlocks[index];

        var what = block.IsAdded ? "Linhas novas" : block.IsDeleted ? "Linhas apagadas" : "Como era antes";
        ChangePeekTitle.Text = _changeBlocks.Count > 1 ? $"{what}  ·  trecho {index + 1} de {_changeBlocks.Count}" : what;
        ChangePeekNavigation.IsVisible = _changeBlocks.Count > 1;
        ChangePeekRevert.Content = block.IsAdded ? "Apagar estas linhas" : block.IsDeleted ? "Trazer de volta" : "Desfazer este trecho";

        if (block.IsAdded)
        {
            ChangePeekEditor.IsVisible = false;
            ChangePeekNote.IsVisible = true;
            ChangePeekNote.Text = block.Count == 1
                ? "Esta linha não existia no último commit."
                : $"Estas {block.Count} linhas não existiam no último commit.";
        }
        else
        {
            var peekDocument = new TextDocument(string.Join('\n', block.OldLines));
            _changePeekColors.Highlighter?.Detach();
            _changePeekColors.Highlighter = _changeBaseText is { } baseText
                ? SyntaxHighlighter.ForExcerpt(document.FullPath, peekDocument, baseText, block.OldFirst)
                : SyntaxHighlighter.ForDocument(document.FullPath, peekDocument);
            _changePeekKinds.Clear();
            _changePeekKinds.AddRange(Enumerable.Repeat(DiffRowKind.Changed, block.OldLines.Count));
            ChangePeekEditor.Document = peekDocument;
            ChangePeekEditor.FontSize = CodeEditor.FontSize;
            var lineHeight = CodeEditor.TextArea.TextView.DefaultLineHeight;
            ChangePeekEditor.MaxHeight = lineHeight * ChangePeekMaxLines + 8;
            ChangePeekEditor.IsVisible = true;
            ChangePeekNote.IsVisible = false;
        }

        // O trecho e o quadro abaixo dele precisam caber na tela.
        var view = CodeEditor.TextArea.TextView;
        var height = view.DefaultLineHeight;
        var peekHeight = 32 + (block.IsAdded ? 30 : Math.Min(block.OldLines.Count, ChangePeekMaxLines) * height + 10);
        var top = view.GetVisualTopByDocumentLine(Math.Max(1, block.IsDeleted ? block.MarkLine : block.First));
        var bottom = view.GetVisualTopByDocumentLine(Math.Min(block.Last, CodeEditor.Document.LineCount)) + height + peekHeight;
        // A cápsula cobre o pé do editor.
        var visible = view.Bounds.Height - 60;
        if (top < CodeEditor.VerticalOffset || bottom > CodeEditor.VerticalOffset + visible)
            CodeEditor.ScrollToVerticalOffset(Math.Max(0, top - height * 2));

        CodeEditor.TextArea.Caret.Location = new TextLocation(Math.Max(1, block.IsDeleted ? block.MarkLine : block.First), 1);
        ChangePeek.BoxShadow = Theming.Palette.Current.PopShadow;
        ChangePeek.IsVisible = true;
        PlaceChangePeek();
        Dispatcher.UIThread.Post(PlaceChangePeek, DispatcherPriority.Loaded);
    }

    // Logo abaixo do trecho, alinhado com o começo do código. Acompanha a rolagem do editor.
    private void PlaceChangePeek()
    {
        if (!ChangePeek.IsVisible || _changePeekIndex is not { } index || index >= _changeBlocks.Count)
            return;
        var view = CodeEditor.TextArea.TextView;
        if (!view.VisualLinesValid)
            return;
        var block = _changeBlocks[index];
        var lineCount = CodeEditor.Document.LineCount;
        double y;
        if (block.IsDeleted)
            y = view.GetVisualTopByDocumentLine(Math.Clamp(block.MarkLine, 1, lineCount));
        else if (block.Last < lineCount)
            y = view.GetVisualTopByDocumentLine(block.Last + 1);
        else
            y = view.GetVisualTopByDocumentLine(lineCount) + view.DefaultLineHeight;
        var point = view.TranslatePoint(new Point(0, y - view.VerticalOffset), SheetHost) ?? default;
        ChangePeek.Margin = new Thickness(point.X, point.Y + 2, 18, 0);
    }

    public void CloseChangePeek()
    {
        if (!ChangePeek.IsVisible && _changePeekIndex is null)
            return;
        ChangePeek.IsVisible = false;
        _changePeekIndex = null;
        _changePeekColors.Highlighter?.Detach();
        _changePeekColors.Highlighter = null;
    }

    private void OnChangePeekPrevious(object? sender, RoutedEventArgs e) => MoveToChange(-1);
    private void OnChangePeekNext(object? sender, RoutedEventArgs e) => MoveToChange(1);

    private void OnChangePeekClose(object? sender, RoutedEventArgs e)
    {
        CloseChangePeek();
        FocusActiveDocument();
    }

    private void OnChangePeekRevert(object? sender, RoutedEventArgs e) => RevertShownChange();

    public void RevertShownChange()
    {
        if (_changePeekIndex is { } index && index < _changeBlocks.Count)
            RevertChanges([_changeBlocks[index]]);
    }

    // Os trechos que a seleção pega, ou o da linha do cursor.
    public List<ChangeBlock> ChangesInSelection()
    {
        var area = CodeEditor.TextArea;
        int from, to;
        if (area.Selection.IsEmpty)
            from = to = area.Caret.Line;
        else
        {
            from = area.Selection.StartPosition.Line;
            to = area.Selection.EndPosition.Line;
            if (from > to)
                (from, to) = (to, from);
        }
        return _changeBlocks.Where(block => block.Touches(from, to)).ToList();
    }

    public void RevertSelectedChanges()
    {
        var blocks = ChangesInSelection();
        if (blocks.Count == 0)
        {
            ShowStatus(_changeBlocks.Count == 0
                ? "Nada mudou neste arquivo desde o último commit."
                : "A seleção não pega nenhum trecho alterado. As faixas coloridas na margem mostram onde estão.");
            return;
        }
        RevertChanges(blocks);
    }

    // Muda só o texto da aba, como digitar: Ctrl Z traz de volta, e o arquivo só muda no disco ao salvar.
    private void RevertChanges(IReadOnlyList<ChangeBlock> blocks)
    {
        if (ActiveDocument is not { IsImage: false } document || !CanEditText)
            return;
        CloseChangePeek();
        document.SetTextKeepingPosition(ChangeBlocks.Revert(document.Document.Text, blocks));
        _ = UpdateGitMarksAsync();
        ShowStatus(blocks.Count == 1
            ? "Trecho de volta como no último commit. Salve para gravar, ou Ctrl Z para desfazer."
            : $"{blocks.Count} trechos de volta como no último commit. Salve para gravar, ou Ctrl Z para desfazer.");
        FocusActiveDocument();
    }
}
