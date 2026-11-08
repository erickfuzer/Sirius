using System.Text.RegularExpressions;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius;

// O Emmet de HTML e de CSS. Como no VS Code, ele aparece na lista de sugestões, e o Enter, o Tab ou um clique
// o aceita; com a lista fechada, o Tab é o de sempre. Os campos depois de expandir são os do mecanismo de trechos.
public partial class MainWindow
{
    public int EmmetStopsLeft => SnippetStopsLeft;
    public void SelectAllForTests() => CodeEditor.SelectAll();

    // A seleção com o cursor numa das pontas, como ao arrastar de baixo para cima.
    public void SelectForTests(int anchor, int caret)
    {
        var area = CodeEditor.TextArea;
        area.Caret.Offset = caret;
        area.Selection = AvaloniaEdit.Editing.Selection.Create(area, anchor, caret);
    }

    // A aba sem nome conta, porque o "!" nela vira página.
    private bool CanUseEmmet => ActiveDocument is { IsImage: false } document
        && (document.IsUntitled || Emmet.Supports(document.FullPath) || CssEmmet.Supports(document.FullPath));

    // O que as abreviações no cursor virariam, sem mexer no texto: a digitada primeiro, depois as da tabela.
    private List<(string Label, EmmetExpansion Expansion)> HtmlEmmetAtCaret(OpenDocument document, bool withTable)
    {
        if (document.IsImage || CodeEditor.IsReadOnly)
            return [];
        var area = CodeEditor.TextArea;
        var text = document.Document;
        var content = text.Text;
        // Na aba sem nome ainda não há extensão. O "!" sozinho já diz que é uma página nova.
        var path = document.IsUntitled && Emmet.IsPageRequest(content, area.Caret.Offset) ? "pagina.html" : document.FullPath;
        var newline = TextUtilities.GetNewLineFromDocument(text, area.Caret.Line);
        return Emmet.SuggestionsAt(content, area.Caret.Offset, path, CodeEditor.Options.IndentationString, newline, withTable);
    }

    private CssExpansion? CssEmmetAtCaret(OpenDocument document) =>
        document.IsImage || CodeEditor.IsReadOnly ? null : CssEmmet.TryExpandAt(document.Document.Text, CodeEditor.TextArea.Caret.Offset, document.FullPath);

    // As linhas da lista. Quem vem do VS Code aperta Enter depois do "!", e foi assim que o dono achou
    // que o Emmet não funcionava.
    private IEnumerable<SuggestionItem> EmmetSuggestions(OpenDocument document)
    {
        if (!CanUseEmmet)
            yield break;
        foreach (var (label, expansion) in HtmlEmmetAtCaret(document, withTable: true))
            yield return new SuggestionItem(label, label, "Emmet", expansion.Text, () => Later(() => ExpandHtmlEmmet(label)));

        if (CssEmmetAtCaret(document) is { } css)
        {
            // No CSS o VS Code mostra o resultado, como "margin: 10px;", e filtra pela abreviação.
            var preview = Snippet.Parse(css.Snippet).Text;
            yield return new SuggestionItem(css.Abbreviation, Regex.Replace(preview, @"\s+", " "), "Emmet", preview, () => Later(() => ExpandCssEmmet()));
        }
    }

    // Depois que a lista fechar, para a troca do texto não cair no meio do fechamento.
    private static void Later(Action action) => Dispatcher.UIThread.Post(action);

    // Pelo mouse: o botão direito e a barra de comando, para não depender de tecla nenhuma.
    public void ExpandEmmet()
    {
        if (ActiveDocument is not { IsImage: false } document || CodeEditor.IsReadOnly)
            return;
        if (ExpandHtmlEmmet(label: null) || ExpandCssEmmet())
            return;
        if (Emmet.IsPageRequest(document.Document.Text, CodeEditor.TextArea.Caret.Offset))
            ShowStatus(Emmet.Supports(document.FullPath)
                ? "O ! só vira página fora de <?php, <style> e <script>."
                : "O ! só vira página em arquivos .html e .php.");
        else if (CssEmmet.Supports(document.FullPath) && !Emmet.Supports(document.FullPath))
            ShowStatus("Ponha o cursor logo depois de uma abreviação dentro das chaves, como m10 ou d:f.");
        else
            ShowStatus("Ponha o cursor logo depois de uma abreviação, como ! ou ul>li.");
    }

    // Sem nome, a abreviação digitada; com nome, a da tabela que a lista mostrou, como a:link.
    private bool ExpandHtmlEmmet(string? label)
    {
        if (ActiveDocument is not { } document)
            return false;
        var found = HtmlEmmetAtCaret(document, withTable: label is not null)
            .Where(s => label is null || s.Label == label).Select(s => s.Expansion).FirstOrDefault();
        if (found is null)
            return false;
        CloseCompletion();
        InsertSnippet(found.Start, found.Length, found.Snippet);
        return true;
    }

    private bool ExpandCssEmmet()
    {
        if (ActiveDocument is not { } document || CssEmmetAtCaret(document) is not { } css)
            return false;
        CloseCompletion();
        InsertSnippet(css.Start, css.Length, ParseSnippetHere(css.Snippet));
        return true;
    }
}
