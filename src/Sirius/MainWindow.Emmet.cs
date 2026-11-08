using Avalonia.Threading;
using AvaloniaEdit.Document;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius;

// O Emmet de HTML. Como no VS Code, ele aparece na lista de sugestões, e o Enter, o Tab ou um clique o aceita;
// com a lista fechada, o Tab é o de sempre. Os campos depois de expandir são os do mecanismo de trechos.
public partial class MainWindow
{
    public int EmmetStopsLeft => SnippetStopsLeft;
    public void SelectAllForTests() => CodeEditor.SelectAll();

    // A aba sem nome conta, porque o "!" nela vira página.
    private bool CanUseEmmet => ActiveDocument is { IsImage: false } document && (document.IsUntitled || Emmet.Supports(document.FullPath));

    // O que a abreviação no cursor viraria, sem mexer no texto. Null quando não há abreviação ali.
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

    // A linha da lista para a abreviação no cursor. Quem vem do VS Code aperta Enter depois do "!",
    // e foi assim que o dono achou que o Emmet não funcionava.
    private SuggestionItem? EmmetSuggestion(OpenDocument document)
    {
        if (!CanUseEmmet || EmmetExpansionAtCaret(document) is not { } expansion)
            return null;
        var abbreviation = document.Document.GetText(expansion.Start, expansion.Length);
        return new SuggestionItem(abbreviation, abbreviation, "Emmet", expansion.Text,
            () => Dispatcher.UIThread.Post(() => { if (ActiveDocument is { } active) ExpandEmmetAtCaret(active); }));
    }

    // Pelo mouse: o botão direito e a barra de comando, para não depender de tecla nenhuma.
    public void ExpandEmmet()
    {
        if (ActiveDocument is not { IsImage: false } document || CodeEditor.IsReadOnly)
            return;
        if (ExpandEmmetAtCaret(document))
            return;
        if (Emmet.IsPageRequest(document.Document.Text, CodeEditor.TextArea.Caret.Offset))
            ShowStatus(Emmet.Supports(document.FullPath)
                ? "O ! só vira página fora de <?php, <style> e <script>."
                : "O ! só vira página em arquivos .html e .php.");
        else
            ShowStatus("Ponha o cursor logo depois de uma abreviação, como ! ou ul>li.");
    }

    private bool ExpandEmmetAtCaret(OpenDocument document)
    {
        if (EmmetExpansionAtCaret(document) is not { } expansion)
            return false;
        CloseCompletion();
        InsertSnippet(expansion.Start, expansion.Length, expansion.ToSnippet());
        return true;
    }
}
