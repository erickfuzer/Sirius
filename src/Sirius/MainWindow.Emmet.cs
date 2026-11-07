using AvaloniaEdit.Document;
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
    private bool TryEmmetTab()
    {
        var area = CodeEditor.TextArea;
        if (ActiveDocument is not { } document || !area.IsKeyboardFocusWithin || !area.Selection.IsEmpty)
            return false;

        if (JumpToNextEmmetStop(document) || ExpandEmmetAtCaret(document))
            return true;

        ExplainRefusedPage(document);
        return false;
    }

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
        var newline = TextUtilities.GetNewLineFromDocument(text, area.Caret.Line);

        // Na aba sem nome ainda não há extensão. O "!" sozinho já diz que é uma página nova.
        var content = text.Text;
        var path = document.IsUntitled && Emmet.IsPageRequest(content, area.Caret.Offset) ? "pagina.html" : document.FullPath;

        if (Emmet.TryExpandAt(content, area.Caret.Offset, path, CodeEditor.Options.IndentationString, newline) is not { } expansion)
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
