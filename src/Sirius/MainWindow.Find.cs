using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Sirius.Core;
using Sirius.Editing;
using Sirius.Theming;

namespace Sirius;

// Procurar e substituir no arquivo aberto, como a caixa do VS Code: Ctrl F, Ctrl H, F3 e Shift F3.
// Os resultados são refeitos só quando alguém pede, depois de cada mudança no texto ou na busca.
public partial class MainWindow
{
    private TextDocument? _findDocument;
    private FindResult? _findResult;
    // De onde a busca começa enquanto se digita: assim "n", "no", "nom" ficam no mesmo lugar.
    private int _findAnchor;

    public bool IsFindOpen => FindBar.IsVisible;
    public bool IsFindReplaceShown => FindReplaceRow.IsVisible;
    public string FindStatusText => FindCount.Text ?? "";

    public string FindText
    {
        get => FindInput.Text ?? "";
        set
        {
            FindInput.Text = value;
            OnFindQueryChanged();
        }
    }

    public string FindReplacement
    {
        get => FindReplaceInput.Text ?? "";
        set => FindReplaceInput.Text = value;
    }

    public bool FindMatchCase { get; private set; }
    public bool FindWholeWord { get; private set; }
    public bool FindUseRegex { get; private set; }

    public IReadOnlyList<FindMatch> FindMatches => EnsureFind().Matches;

    private FindOptions CurrentFindOptions => new(FindText, FindMatchCase, FindWholeWord, FindUseRegex);

    private void InitializeFind()
    {
        // A busca vale para o lado com o foco: o outro não marca nada.
        foreach (var pane in _panes)
        {
            var area = pane.Editor.TextArea;
            area.TextView.BackgroundRenderers.Add(new FindMatchRenderer(() => pane == _pane && IsFindOpen ? FindMatches : []));
            area.DocumentChanged += (_, _) =>
            {
                if (pane == _pane)
                    WatchFindDocument();
            };
            area.SelectionChanged += (_, _) =>
            {
                if (pane == _pane)
                    UpdateFindStatus();
            };
        }
        FindInput.TextChanged += (_, _) => OnFindQueryChanged();
        FindInput.GotFocus += (_, _) => _findAnchor = SelectionStartOrCaret();
        WatchFindDocument();
    }

    private void WatchFindDocument()
    {
        if (_findDocument is not null)
            _findDocument.TextChanged -= OnFindDocumentChanged;
        _findDocument = CurrentEditor.Document;
        if (_findDocument is not null)
            _findDocument.TextChanged += OnFindDocumentChanged;
        InvalidateFind();
    }

    private void OnFindDocumentChanged(object? sender, EventArgs e) => InvalidateFind();

    private void InvalidateFind()
    {
        _findResult = null;
        if (!IsFindOpen)
            return;
        // A contagem espera a edição terminar; o desenho pede os resultados por conta própria.
        Dispatcher.UIThread.Post(UpdateFindStatus, DispatcherPriority.Background);
        CurrentEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
    }

    private FindResult EnsureFind()
    {
        if (_findResult is not null)
            return _findResult;
        if (!IsFindOpen || ActiveDocument is not { IsImage: false } || CurrentEditor.Document is not { } document)
            return FindResult.Empty;
        return _findResult = FindInText.FindAll(document.Text, CurrentFindOptions);
    }

    // Abrir e fechar

    public void OpenFind(bool replace = false)
    {
        if (ActiveDocument is not { IsImage: false } || IsDiffOpen)
            return;

        var (seed, anchor) = FindSeed();
        FindBar.IsVisible = true;
        ShowFindReplace(replace);
        _findAnchor = anchor;
        if (seed is not null && seed != FindText)
            FindInput.Text = seed;
        OnFindQueryChanged();

        var box = replace && FindText.Length > 0 ? FindReplaceInput : FindInput;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    public void CloseFind()
    {
        if (!IsFindOpen)
            return;
        var hadFocus = FindBar.IsKeyboardFocusWithin;
        FindBar.IsVisible = false;
        _findResult = null;
        CurrentEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        if (hadFocus)
            FocusActiveDocument();
    }

    // Trocar de aba refaz a busca no arquivo novo; numa imagem, a barra some.
    private void RefreshFindForDocument()
    {
        if (!IsFindOpen)
            return;
        if (ActiveDocument is not { IsImage: false })
        {
            CloseFind();
            return;
        }
        InvalidateFind();
        UpdateFindStatus();
    }

    private void ShowFindReplace(bool show)
    {
        FindReplaceRow.IsVisible = show;
        // Ocupando as duas linhas com a de baixo escondida, o Grid dava metade da altura dele à linha vazia,
        // e sobrava uma faixa embaixo da barra.
        Grid.SetRowSpan(FindExpand, show ? 2 : 1);
        FindExpand.Height = show ? 62 : 28;
        // A seta aponta para o lado com o substituir fechado e para baixo com ele aberto, como no VS Code.
        FindExpandIcon.Data = Avalonia.Media.Geometry.Parse(show ? "M0 0L4 4L8 0" : "M0 0L4 4L0 8");
    }

    // Como o VS Code: a seleção de uma linha vira a busca; sem seleção, a palavra sob o cursor.
    private (string? Seed, int Anchor) FindSeed()
    {
        var area = CurrentEditor.TextArea;
        var document = area.Document;
        if (!area.Selection.IsEmpty)
        {
            var segment = area.Selection.SurroundingSegment;
            var text = document.GetText(segment);
            return text.Length <= 200 && !text.Contains('\n') && !text.Contains('\r') ? (text, segment.Offset) : (null, segment.Offset);
        }

        var caret = area.Caret.Offset;
        int start = caret, end = caret;
        while (start > 0 && IsWordChar(document.GetCharAt(start - 1)))
            start--;
        while (end < document.TextLength && IsWordChar(document.GetCharAt(end)))
            end++;
        return end > start ? (document.GetText(start, end - start), start) : (null, caret);
    }

    private int SelectionStartOrCaret()
    {
        var area = CurrentEditor.TextArea;
        return area.Selection.IsEmpty ? area.Caret.Offset : area.Selection.SurroundingSegment.Offset;
    }

    // Mudou o texto ou uma opção: refaz e seleciona o primeiro resultado a partir de onde a busca começou.
    private void OnFindQueryChanged()
    {
        _findResult = null;
        if (!IsFindOpen)
            return;
        var matches = FindMatches;
        if (matches.Count > 0)
            SelectFindMatch(matches[FindInText.IndexFrom(matches, _findAnchor)]);
        UpdateFindStatus();
        CurrentEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
    }

    // Andar pelos resultados

    // F3 com a barra fechada abre a barra sem tirar o foco do código, como quem só quer o próximo.
    public void FindStep(int step)
    {
        if (ActiveDocument is not { IsImage: false } || IsDiffOpen)
            return;
        if (!IsFindOpen)
        {
            if (FindText.Length == 0)
            {
                OpenFind();
                return;
            }
            FindBar.IsVisible = true;
            _findResult = null;
        }

        var matches = FindMatches;
        if (matches.Count > 0)
        {
            var area = CurrentEditor.TextArea;
            var index = step > 0
                ? FindInText.IndexFrom(matches, area.Selection.IsEmpty ? area.Caret.Offset : area.Selection.SurroundingSegment.EndOffset)
                : FindInText.IndexBefore(matches, SelectionStartOrCaret());
            SelectFindMatch(matches[index]);
            _findAnchor = matches[index].Offset;
        }
        UpdateFindStatus();
        CurrentEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
    }

    private void SelectFindMatch(FindMatch match)
    {
        CurrentEditor.Select(match.Offset, match.Length);
        CurrentEditor.TextArea.Caret.Offset = match.End;
        var location = CurrentEditor.Document.GetLocation(match.Offset);
        CurrentEditor.ScrollTo(location.Line, location.Column);
        // A barra flutua sobre as primeiras linhas: o resultado não pode ficar escondido atrás dela.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsFindOpen)
                return;
            var view = CurrentEditor.TextArea.TextView;
            var top = view.GetVisualTopByDocumentLine(location.Line) - CurrentEditor.VerticalOffset;
            var covered = FindBar.Bounds.Bottom + 4;
            if (top < covered && CurrentEditor.VerticalOffset > 0)
                CurrentEditor.ScrollToVerticalOffset(Math.Max(0, CurrentEditor.VerticalOffset - (covered - top)));
        }, DispatcherPriority.Loaded);
    }

    // A posição do resultado selecionado, ou -1 quando a seleção não é um resultado.
    public int CurrentFindIndex
    {
        get
        {
            var area = CurrentEditor.TextArea;
            if (area.Selection.IsEmpty)
                return -1;
            var segment = area.Selection.SurroundingSegment;
            return FindInText.IndexOf(FindMatches, segment.Offset, segment.Length);
        }
    }

    private void UpdateFindStatus()
    {
        if (!IsFindOpen)
            return;
        var result = EnsureFind();
        var count = result.Matches.Count;
        var plus = result.Truncated ? "+" : "";
        var index = count > 0 ? CurrentFindIndex : -1;
        FindCount.Text = FindText.Length == 0 ? ""
            : result.Invalid ? "Expressão inválida"
            : count == 0 ? "Nenhum resultado"
            : index >= 0 ? $"{index + 1} de {count}{plus}"
            : count == 1 ? "1 resultado"
            : $"{count}{plus} resultados";
        FindCount.Foreground = FindText.Length > 0 && (result.Invalid || count == 0) ? Palette.Current.Error : Palette.Current.Muted;
    }

    // Substituir

    // Como no VS Code: se a seleção é um resultado, troca e vai ao próximo; se não, só vai ao próximo.
    public void ReplaceOne()
    {
        if (!IsFindOpen || ActiveDocument is not { IsImage: false } || CurrentEditor.Document is not { } document)
            return;
        var matches = FindMatches;
        if (matches.Count == 0)
            return;

        var index = CurrentFindIndex;
        if (index < 0)
        {
            SelectFindMatch(matches[FindInText.IndexFrom(matches, SelectionStartOrCaret())]);
            UpdateFindStatus();
            return;
        }

        var match = matches[index];
        var text = FindInText.ReplacementFor(document.Text, match, CurrentFindOptions, FindReplacement);
        document.Replace(match.Offset, match.Length, text);
        _findResult = null;
        var after = match.Offset + text.Length;
        var remaining = FindMatches;
        if (remaining.Count > 0)
        {
            var next = remaining[FindInText.IndexFrom(remaining, after)];
            SelectFindMatch(next);
            _findAnchor = next.Offset;
        }
        else
        {
            CurrentEditor.TextArea.ClearSelection();
            CurrentEditor.TextArea.Caret.Offset = after;
        }
        UpdateFindStatus();
        CurrentEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
    }

    // Tudo num passo só do Ctrl Z. Vale também além do limite de resultados marcados.
    public int ReplaceAll()
    {
        if (!IsFindOpen || ActiveDocument is not { IsImage: false } || CurrentEditor.Document is not { } document)
            return 0;
        var result = EnsureFind();
        if (result.Invalid || result.Matches.Count == 0)
            return 0;

        var plan = FindInText.ReplaceAllPlan(document.Text, CurrentFindOptions, FindReplacement);
        if (plan.Count == 0)
            return 0;
        using (document.RunUpdate())
        {
            foreach (var (match, text) in plan)
                document.Replace(match.Offset, match.Length, text);
        }
        _findResult = null;
        UpdateFindStatus();
        ShowStatus(plan.Count == 1 ? "1 troca feita" : $"{plan.Count} trocas feitas");
        return plan.Count;
    }

    // Teclado

    // Chamado pelo teclado da janela, antes de tudo que é do editor.
    private bool HandleFindKey(KeyEventArgs e)
    {
        if (IsPaletteOpen || IsSettingsOpen || TerminalHost.IsFocused)
            return false;
        var mods = e.KeyModifiers;
        var inBar = FindBar.IsVisible && FindBar.IsKeyboardFocusWithin;

        if (e.Key == Key.F3 && (mods == KeyModifiers.None || mods == KeyModifiers.Shift))
        {
            FindStep(mods == KeyModifiers.Shift ? -1 : 1);
            return true;
        }

        // O AltGr chega como Ctrl e Alt juntos, então Ctrl F e Ctrl H não aceitam o Alt.
        if (mods == KeyModifiers.Control && e.Key is Key.F or Key.H && ActiveDocument is { IsImage: false })
        {
            OpenFind(replace: e.Key == Key.H);
            return true;
        }

        if (inBar)
        {
            if (e.Key == Key.Escape)
            {
                CloseFind();
                return true;
            }
            if (e.Key == Key.Enter && mods == (KeyModifiers.Control | KeyModifiers.Alt) && IsFindReplaceShown)
            {
                ReplaceAll();
                return true;
            }
            if (e.Key == Key.Enter && FindReplaceInput.IsFocused && mods == KeyModifiers.None)
            {
                ReplaceOne();
                return true;
            }
            if (e.Key == Key.Enter && FindInput.IsFocused && (mods == KeyModifiers.None || mods == KeyModifiers.Shift))
            {
                FindStep(mods == KeyModifiers.Shift ? -1 : 1);
                return true;
            }
            if (mods == KeyModifiers.Alt && e.Key is Key.C or Key.W or Key.R)
            {
                ToggleFindOption(e.Key);
                return true;
            }
        }

        // Esc no código também fecha a barra, quando não há lista nem trecho com campos para fechar antes.
        if (e.Key == Key.Escape && mods == KeyModifiers.None && IsFindOpen && CurrentEditor.TextArea.IsKeyboardFocusWithin
            && _completion is null && !IsInSnippet)
        {
            CloseFind();
            return true;
        }
        return false;
    }

    private void ToggleFindOption(Key key)
    {
        switch (key)
        {
            case Key.C: FindMatchCase = !FindMatchCase; break;
            case Key.W: FindWholeWord = !FindWholeWord; break;
            case Key.R: FindUseRegex = !FindUseRegex; break;
        }
        FindCaseButton.Classes.Set("on", FindMatchCase);
        FindWordButton.Classes.Set("on", FindWholeWord);
        FindRegexButton.Classes.Set("on", FindUseRegex);
        OnFindQueryChanged();
    }

    public void SetFindOptions(bool matchCase, bool wholeWord, bool useRegex)
    {
        if (FindMatchCase != matchCase) ToggleFindOption(Key.C);
        if (FindWholeWord != wholeWord) ToggleFindOption(Key.W);
        if (FindUseRegex != useRegex) ToggleFindOption(Key.R);
    }

    // Botões

    private void OnFindToggleCase(object? sender, RoutedEventArgs e) => ToggleFindOption(Key.C);
    private void OnFindToggleWord(object? sender, RoutedEventArgs e) => ToggleFindOption(Key.W);
    private void OnFindToggleRegex(object? sender, RoutedEventArgs e) => ToggleFindOption(Key.R);
    private void OnFindPrevious(object? sender, RoutedEventArgs e) => FindStep(-1);
    private void OnFindNext(object? sender, RoutedEventArgs e) => FindStep(1);
    private void OnFindClose(object? sender, RoutedEventArgs e) => CloseFind();
    private void OnFindReplaceOne(object? sender, RoutedEventArgs e) => ReplaceOne();
    private void OnFindReplaceAll(object? sender, RoutedEventArgs e) => ReplaceAll();

    private void OnFindExpand(object? sender, RoutedEventArgs e)
    {
        ShowFindReplace(!IsFindReplaceShown);
        if (IsFindReplaceShown)
            Dispatcher.UIThread.Post(() => FindReplaceInput.Focus());
    }
}
