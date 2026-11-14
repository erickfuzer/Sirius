using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Sirius.Editing;
using Sirius.Theming;

namespace Sirius;

// Dois arquivos lado a lado. Cada lado é um editor completo (EditorPane), e o que tem o foco é "o" editor:
// tudo o que a Sirius faz no código (sugestões, Emmet, busca, salvar, depuração) passa por CurrentEditor.
// As abas são uma fileira só: clicar numa abre o arquivo no lado com o foco.
public partial class MainWindow
{
    private EditorPane[] _panes = null!;
    private EditorPane _pane = null!;

    private TextEditor CurrentEditor => _pane.Editor;
    private GutterMargin Gutter => _pane.Gutter;
    private TypographicColorizer Colorizer => _pane.Colorizer;
    private CssColorGenerator Swatches => _pane.Swatches;

    public bool IsSplit => SecondaryPane.IsVisible;
    public bool IsRightSideFocused => _pane == _panes[1];
    public OpenDocument? LeftDocument => _panes[0].Document;
    public OpenDocument? RightDocument => IsSplit ? _panes[1].Document : null;
    public TextEditor LeftEditor => _panes[0].Editor;
    public TextEditor RightEditor => _panes[1].Editor;
    public bool IsSplitButtonVisible => SplitButton.IsVisible;

    private void InitializePanes()
    {
        _panes = [new EditorPane(CodeEditor, "esquerdo"), new EditorPane(SecondaryEditor, "direito")];
        _pane = _panes[0];

        var options = CodeEditor.Options;
        options.AllowScrollBelowDocument = true;
        options.ConvertTabsToSpaces = true;
        options.EnableHyperlinks = false;
        options.EnableEmailHyperlinks = false;
        options.HighlightCurrentLine = false;
        // As mesmas opções nos dois lados: recuo, tabulação e o resto mudam juntos.
        SecondaryEditor.Options = options;

        foreach (var pane in _panes)
        {
            var area = pane.Editor.TextArea;
            // Primeiro de tudo: um clique no outro lado já o torna "o" editor, antes de o clique fazer o resto,
            // como o Ctrl + clique da definição ou o ponto de parada na margem.
            pane.Editor.AddHandler(PointerPressedEvent, (_, _) => FocusPane(pane), RoutingStrategies.Tunnel, handledEventsToo: true);
            area.GotFocus += (_, _) => FocusPane(pane);
            area.LeftMargins.Insert(0, pane.Gutter);
            area.TextView.LineTransformers.Add(pane.Colorizer);
            area.Caret.PositionChanged += (_, _) =>
            {
                if (pane == _pane)
                    UpdateCaretPosition();
            };
        }
        SecondaryPane.SizeChanged += (_, _) => PlaceFindBar();
    }

    // O lado clicado vira "o" editor, com o arquivo dele na aba destacada, no caminho e na cápsula.
    private void FocusPane(EditorPane pane)
    {
        if (pane == _pane || (pane == _panes[1] && !IsSplit))
            return;
        CloseCompletion();
        CloseColorPicker();
        CloseChangePeek();
        _pane = pane;
        ActiveDocument = pane.Document;
        WatchFindDocument();
        AfterActiveChanged();
        // As marcas da busca saem do lado que perdeu o foco e vão para o que ganhou.
        ForEachEditor(editor => editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection));
    }

    // Carrega um documento num lado, guardando antes onde estava o cursor no que saía.
    private static void LoadInPane(EditorPane pane, OpenDocument? document)
    {
        if (pane.Document is { } previous && previous != document)
        {
            previous.CaretOffset = pane.Editor.TextArea.Caret.Offset;
            previous.VerticalOffset = pane.Editor.VerticalOffset;
        }
        pane.Document = document;
        if (document is null)
            return;
        pane.Colorizer.Highlighter = document.Highlighter;
        pane.Editor.Document = document.Document;
        pane.Editor.TextArea.Caret.Offset = Math.Min(document.CaretOffset, document.Document.TextLength);
        pane.Editor.ScrollToVerticalOffset(document.VerticalOffset);
        pane.Gutter.SnapThread();
        pane.Editor.TextArea.TextView.Redraw();
    }

    // Um arquivo que fechou, ou que outra aba tomou, sai do lado que não tem o foco.
    private void ForgetInOtherPanes(OpenDocument document, int index)
    {
        foreach (var pane in _panes)
        {
            if (pane == _pane || pane.Document != document)
                continue;
            if (pane == _panes[1])
            {
                CloseSplit();
                continue;
            }
            var next = _documents.Where(d => d != document).ElementAtOrDefault(Math.Min(index, _documents.Count - 1))
                ?? _documents.FirstOrDefault(d => d != document);
            pane.Document = null;
            LoadInPane(pane, next is { IsImage: false } ? next : null);
            pane.Editor.IsVisible = pane.Document is not null;
        }
        UpdatePaneHeaders();
    }

    // Abrir e fechar o lado direito

    private void OnSplitButton(object? sender, RoutedEventArgs e) => ToggleSplit();

    private void OnCloseSplit(object? sender, RoutedEventArgs e) => CloseSplit();

    public void ToggleSplit()
    {
        if (IsSplit)
            CloseSplit();
        else
            OpenSplit();
    }

    // Como no VS Code: o arquivo de agora abre também à direita, que fica com o foco. A próxima aba
    // clicada vai para lá.
    public void OpenSplit(OpenDocument? document = null)
    {
        document ??= ActiveDocument;
        if (document is not { IsImage: false })
        {
            ShowStatus("Abra um arquivo de texto para dividir o editor.");
            return;
        }
        if (!IsSplit)
        {
            SplitGrid.ColumnDefinitions[1].Width = GridLength.Auto;
            SplitGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            SplitDivider.IsVisible = true;
            SecondaryPane.IsVisible = true;
        }
        CloseCompletion();
        CloseColorPicker();
        CloseChangePeek();
        _pane = _panes[1];
        ActiveDocument = _pane.Document;
        Activate(document);
    }

    public void CloseSplit()
    {
        if (!IsSplit)
            return;
        var right = _panes[1];
        LoadInPane(right, null);
        // Sem documento, o editor da direita não segura o texto de uma aba que talvez já tenha fechado.
        right.Editor.Document = new TextDocument();
        SplitGrid.ColumnDefinitions[1].Width = new GridLength(0);
        SplitGrid.ColumnDefinitions[2].Width = new GridLength(0);
        SplitDivider.IsVisible = false;
        SecondaryPane.IsVisible = false;
        if (_pane == right)
        {
            CloseCompletion();
            CloseColorPicker();
            CloseChangePeek();
            _pane = _panes[0];
            ActiveDocument = _pane.Document;
            AfterActiveChanged();
            Dispatcher.UIThread.Post(FocusActiveDocument);
        }
        else
        {
            UpdatePaneHeaders();
            PlaceFindBar();
        }
    }

    // Abre um arquivo no lado direito, como o "Abrir ao lado" da lista de arquivos.
    public void OpenFileToSide(string path)
    {
        if (!IsSplit)
        {
            if (ActiveDocument is not { IsImage: false })
            {
                OpenFile(path);
                return;
            }
            OpenSplit();
        }
        else if (_pane != _panes[1])
        {
            FocusPane(_panes[1]);
        }
        OpenFile(path);
    }

    private void RevealInOtherSide(string path)
    {
        if (!IsSplit || string.Equals(ActiveDocument?.FullPath, path, StringComparison.OrdinalIgnoreCase))
            return;
        var other = _pane == _panes[0] ? _panes[1] : _panes[0];
        if (string.Equals(other.Document?.FullPath, path, StringComparison.OrdinalIgnoreCase))
            FocusPane(other);
    }

    public void FocusOtherSide()
    {
        if (IsSplit)
        {
            FocusPane(_pane == _panes[0] ? _panes[1] : _panes[0]);
            FocusActiveDocument();
        }
    }

    private void OnPaneHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string tag } && int.TryParse(tag, out var index) && index < _panes.Length)
        {
            FocusPane(_panes[index]);
            FocusActiveDocument();
        }
    }

    // O nome do arquivo em cima de cada lado; o do lado com o foco aparece mais forte.
    private void UpdatePaneHeaders()
    {
        PrimaryHeader.IsVisible = IsSplit;
        SplitButton.IsVisible = ActiveDocument is { IsImage: false } && !IsDiffOpen;
        SplitButton.Classes.Set("on", IsSplit);
        if (!IsSplit)
            return;
        var palette = Palette.Current;
        foreach (var (pane, name, icon) in new[] { (_panes[0], PrimaryHeaderName, PrimaryHeaderIcon), (_panes[1], SecondaryHeaderName, SecondaryHeaderIcon) })
        {
            var focused = pane == _pane;
            name.Text = pane.Document?.Name ?? "";
            name.Foreground = focused ? palette.Ink : palette.Muted;
            name.FontWeight = focused ? FontWeight.SemiBold : FontWeight.Normal;
            icon.Source = pane.Document is { } document ? FileIcons.For(document.Name) : null;
            icon.Opacity = focused ? 1 : 0.6;
        }
    }

    // A barra de busca fica em cima do lado com o foco, abaixo do nome do arquivo.
    private void PlaceFindBar()
    {
        var right = 22.0;
        if (IsSplit && _pane == _panes[0])
            right += SecondaryPane.Bounds.Width + SplitDivider.Bounds.Width;
        FindBar.Margin = new Thickness(0, IsSplit ? 34 : 6, right, 0);
    }

    // Um arquivo renomeado pode trocar de linguagem: cada lado pega a sintaxe de novo.
    private void RefreshPaneHighlighters()
    {
        foreach (var pane in _panes)
        {
            if (pane.Document is { } document)
                pane.Colorizer.Highlighter = document.Highlighter;
            pane.Editor.TextArea.TextView.Redraw();
        }
        UpdatePaneHeaders();
    }

    // Fonte, quebra de linha e cores valem para os dois lados.
    private void ForEachEditor(Action<TextEditor> action)
    {
        foreach (var pane in _panes)
            action(pane.Editor);
    }
}
