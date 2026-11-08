using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius;

public partial class MainWindow
{
    // As próximas paradas do Tab na última expansão, que acompanham o texto conforme se digita.
    private readonly List<TextAnchor> _emmetStops = [];
    private TextAnchor? _emmetStart;
    private TextAnchor? _emmetEnd;
    private TextDocument? _emmetDocument;

    public int EmmetStopsLeft => _emmetStops.Count;
    public void SelectAllForTests() => CodeEditor.SelectAll();

    // true quando o Tab foi usado aqui. Com false, ele segue seu caminho normal.
    // O "!" se digita com Shift, e quem ainda o segura ao bater o Tab manda Shift Tab. Por isso o "!" sozinho
    // também vira página com Shift Tab. As outras abreviações não: ali o Shift Tab continua recuando a linha.
    private bool TryEmmetTab(bool shift = false)
    {
        var area = CodeEditor.TextArea;
        if (ActiveDocument is not { } document || !area.IsKeyboardFocusWithin || !area.Selection.IsEmpty)
            return false;
        if (shift && !Emmet.IsPageRequest(document.Document.Text, area.Caret.Offset))
            return false;

        if ((!shift && JumpToNextEmmetStop(document)) || ExpandEmmetAtCaret(document))
            return true;

        ExplainRefusedPage(document);
        return false;
    }

    // O que o Tab faria agora no cursor, sem mexer no texto. Null quando não há abreviação ali.
    private EmmetExpansion? EmmetExpansionAtCaret(OpenDocument document)
    {
        if (document.IsImage || CodeEditor.IsReadOnly)
            return null;
        var area = CodeEditor.TextArea;
        var text = document.Document;
        var content = text.Text;
        // Na aba sem nome ainda não há extensão. O "!" sozinho já diz que é uma página nova.
        var path = document.IsUntitled && Emmet.IsPageRequest(content, area.Caret.Offset) ? "pagina.html" : document.FullPath;
        var newline = TextUtilities.GetNewLineFromDocument(text, area.Caret.Line);
        return Emmet.TryExpandAt(content, area.Caret.Offset, path, CodeEditor.Options.IndentationString, newline);
    }

    // Como no VS Code: a abreviação aparece na lista de sugestões, e o Enter ou um clique a aceita.
    // Quem vem de lá aperta Enter depois do "!", e foi assim que o dono achou que não funcionava.
    private EmmetCompletionItem? EmmetSuggestion(OpenDocument document)
    {
        if (!CanUseEmmet || EmmetExpansionAtCaret(document) is not { } expansion)
            return null;
        var abbreviation = document.Document.GetText(expansion.Start, expansion.Length);
        return new EmmetCompletionItem(abbreviation, expansion.Text,
            () => Dispatcher.UIThread.Post(() => { if (ActiveDocument is { } active) ExpandEmmetAtCaret(active); }));
    }

    // A linha da sugestão: a abreviação, "Emmet" do lado e, ao parar nela, o HTML que vai ser escrito.
    private sealed class EmmetCompletionItem(string abbreviation, string expanded, Action expand) : ICompletionData
    {
        private const int PreviewLines = 14;

        public IImage? Image => null;
        public string Text => abbreviation;
        public double Priority => 1;

        // Largura mínima para o "Emmet" ir para a direita, como no VS Code, mesmo numa lista só com ele.
        public object Content => new DockPanel
        {
            MinWidth = 220,
            Children =
            {
                new TextBlock { Text = "Emmet", FontSize = 11, Foreground = Theming.Palette.Current.Muted, Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right },
                new TextBlock { Text = abbreviation, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };

        public object Description
        {
            get
            {
                var lines = expanded.Split('\n');
                var preview = string.Join('\n', lines.Take(PreviewLines)).Replace("\r", "");
                if (lines.Length > PreviewLines)
                    preview += "\n…";
                return new TextBlock { Text = preview, FontFamily = new FontFamily("Consolas, monospace"), FontSize = 12, MaxWidth = 420, TextWrapping = TextWrapping.Wrap };
            }
        }

        // O texto ainda não mudou aqui: quem expande é o mesmo caminho do Tab, logo depois de a lista fechar.
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => expand();
    }

    // Pelo mouse: o botão direito e a barra de comando, para não depender da tecla Tab.
    public void ExpandEmmet()
    {
        if (ActiveDocument is not { IsImage: false } document || CodeEditor.IsReadOnly)
            return;
        if (ExpandEmmetAtCaret(document))
            return;
        if (Emmet.IsPageRequest(document.Document.Text, CodeEditor.TextArea.Caret.Offset))
            ExplainRefusedPage(document);
        else
            ShowStatus("Ponha o cursor logo depois de uma abreviação, como ! ou ul>li.");
    }

    // A aba sem nome conta, porque o "!" nela vira página.
    private bool CanUseEmmet => ActiveDocument is { IsImage: false } document && (document.IsUntitled || Emmet.Supports(document.FullPath));

    // Sem isto, o Tab só anda alguns espaços, e parece que o Emmet não funciona.
    private void ExplainRefusedPage(OpenDocument document)
    {
        var text = document.Document.Text;
        if (!Emmet.IsPageRequest(text, CodeEditor.TextArea.Caret.Offset))
            return;
        ShowStatus(Emmet.Supports(document.FullPath)
            ? "O ! só vira página fora de <?php, <style> e <script>."
            : "O ! só vira página em arquivos .html e .php.");
    }

    private bool ExpandEmmetAtCaret(OpenDocument document)
    {
        var area = CodeEditor.TextArea;
        var text = document.Document;
        if (EmmetExpansionAtCaret(document) is not { } expansion)
            return false;

        CloseCompletion();
        ClearEmmetStops();
        text.Replace(expansion.Start, expansion.Length, expansion.Text);

        _emmetDocument = text;
        _emmetStart = text.CreateAnchor(expansion.Start);
        _emmetStart.SurviveDeletion = true;
        _emmetEnd = text.CreateAnchor(expansion.Start + expansion.Text.Length);
        _emmetEnd.MovementType = AnchorMovementType.AfterInsertion;
        _emmetEnd.SurviveDeletion = true;
        foreach (var stop in expansion.Stops)
        {
            var anchor = text.CreateAnchor(stop);
            anchor.SurviveDeletion = true;
            _emmetStops.Add(anchor);
        }

        // Sem campo vazio, o cursor fica no fim do que foi escrito.
        area.Caret.Offset = _emmetEnd.Offset;
        JumpToNextEmmetStop(document);
        return true;
    }

    private bool JumpToNextEmmetStop(OpenDocument document)
    {
        if (_emmetStops.Count == 0)
            return false;

        // Trocou de arquivo, ou saiu do trecho expandido: o Tab volta ao normal.
        var caret = CodeEditor.TextArea.Caret.Offset;
        if (_emmetDocument != document.Document || caret < _emmetStart!.Offset || caret > _emmetEnd!.Offset)
        {
            ClearEmmetStops();
            return false;
        }

        CodeEditor.TextArea.Caret.Offset = _emmetStops[0].Offset;
        _emmetStops.RemoveAt(0);
        return true;
    }

    private void ClearEmmetStops()
    {
        _emmetStops.Clear();
        _emmetStart = null;
        _emmetEnd = null;
        _emmetDocument = null;
    }
}
