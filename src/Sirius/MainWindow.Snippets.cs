using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Sirius.Core;

namespace Sirius;

// Trechos com campos, como no VS Code: o texto do campo entra selecionado, o Tab vai para o próximo,
// o Shift Tab volta, o Esc sai, e o mesmo campo repetido acompanha o que se digita. Um caminho só para
// o Emmet, os atalhos de cada linguagem e as sugestões dos servidores.
public partial class MainWindow
{
    private sealed record SnippetField(int Index, TextAnchor Start, TextAnchor End);

    private sealed class SnippetSession(TextDocument document, List<SnippetField> fields, IReadOnlyList<int> order, TextAnchor begin, TextAnchor finish)
    {
        public TextDocument Document { get; } = document;
        public List<SnippetField> Fields { get; } = fields;
        public IReadOnlyList<int> Order { get; } = order;
        public TextAnchor Begin { get; } = begin;
        public TextAnchor Finish { get; } = finish;
        public int Position { get; set; }
        public int CurrentIndex => Order[Position];
    }

    private SnippetSession? _snippet;
    private bool _movingInSnippet;
    private bool _mirroring;

    public bool IsInSnippet => _snippet is not null;
    public int SnippetStopsLeft => _snippet is { } session ? session.Order.Count - 1 - session.Position : 0;

    // O texto que a variável $TM_SELECTED_TEXT, o nome do arquivo e as outras dos trechos do VS Code representam.
    private string? SnippetVariable(string name)
    {
        var area = CodeEditor.TextArea;
        var path = ActiveDocument?.FullPath;
        return name switch
        {
            "TM_SELECTED_TEXT" => area.Selection.IsEmpty ? null : area.Selection.GetText(),
            "TM_FILENAME" => path is null ? null : Path.GetFileName(path),
            "TM_FILENAME_BASE" => path is null ? null : Path.GetFileNameWithoutExtension(path),
            "TM_DIRECTORY" => path is null ? null : Path.GetDirectoryName(path),
            "TM_FILEPATH" => path,
            "TM_LINE_NUMBER" => area.Caret.Line.ToString(),
            "TM_LINE_INDEX" => (area.Caret.Line - 1).ToString(),
            "WORKSPACE_NAME" => RootFolder is null ? null : Path.GetFileName(RootFolder.TrimEnd(Path.DirectorySeparatorChar)),
            "CURRENT_YEAR" => DateTime.Now.Year.ToString(),
            "UUID" => Guid.NewGuid().ToString(),
            _ => null,
        };
    }

    // Os atalhos da linguagem no cursor, como foreach no PHP e log no JavaScript. Como no VS Code, não aparecem
    // em texto entre aspas nem em comentário, e no Markdown só pedindo, com Ctrl Espaço ou pela barra.
    private IEnumerable<Editing.SuggestionItem> LibrarySuggestions(Editing.OpenDocument document, bool explicitRequest)
    {
        if (document.IsImage || CodeEditor.IsReadOnly)
            yield break;
        var text = document.Document.Text;
        var caret = CodeEditor.TextArea.Caret.Offset;
        if (SnippetLibrary.LanguageAt(document.FullPath, text, caret) is not { } language)
            yield break;
        if (language == "markdown" && !explicitRequest)
            yield break;
        if (SnippetLibrary.WordBefore(text, caret) is not { } word || (word.Word.Length == 0 && !explicitRequest))
            yield break;
        if (AutoPairs.IsInStringOrComment(text, word.Start))
            yield break;
        foreach (var entry in SnippetLibrary.Matching(language, word.Word))
            yield return new Editing.SuggestionItem(entry.Prefix, entry.Prefix, "Trecho", Snippet.Parse(entry.Body, SnippetVariable).Text,
                () => Dispatcher.UIThread.Post(() => InsertLibrarySnippet(entry.Body)));
    }

    // A palavra é lida de novo, porque a pessoa pode ter digitado mais com a lista aberta.
    private void InsertLibrarySnippet(string body)
    {
        var text = CodeEditor.Document.Text;
        var caret = CodeEditor.TextArea.Caret.Offset;
        var start = SnippetLibrary.WordBefore(text, caret)?.Start ?? caret;
        CloseCompletion();
        InsertSnippet(start, caret - start, ParseSnippetHere(body));
    }

    // Lê um corpo de trecho já no recuo e na quebra de linha do arquivo, a partir da linha do cursor.
    private SnippetText ParseSnippetHere(string body)
    {
        var area = CodeEditor.TextArea;
        var document = area.Document;
        var line = document.GetLineByNumber(area.Caret.Line);
        var leading = document.GetText(TextUtilities.GetLeadingWhitespace(document, line));
        var newline = TextUtilities.GetNewLineFromDocument(document, area.Caret.Line);
        return Snippet.Parse(body, SnippetVariable, CodeEditor.Options.IndentationString, newline, leading);
    }

    // Troca o trecho [start, start + length) pelo texto, num passo só de desfazer, e começa pelos campos.
    public void InsertSnippet(int start, int length, SnippetText snippet)
    {
        EndSnippet();
        var area = CodeEditor.TextArea;
        var document = area.Document;
        document.Replace(start, length, snippet.Text);

        var order = snippet.Order;
        if (order.Count == 0)
        {
            area.ClearSelection();
            area.Caret.Offset = start + snippet.Text.Length;
            return;
        }

        TextAnchor Anchor(int offset, AnchorMovementType movement)
        {
            var anchor = document.CreateAnchor(offset);
            anchor.MovementType = movement;
            anchor.SurviveDeletion = true;
            return anchor;
        }

        // O começo fica antes do que se digitar ali, e o fim depois: o campo cresce com o texto.
        var fields = snippet.Stops
            .Select(stop => new SnippetField(stop.Index,
                Anchor(start + stop.Offset, AnchorMovementType.BeforeInsertion),
                Anchor(start + stop.Offset + stop.Length, AnchorMovementType.AfterInsertion)))
            .ToList();
        var session = new SnippetSession(document, fields, order,
            Anchor(start, AnchorMovementType.BeforeInsertion), Anchor(start + snippet.Text.Length, AnchorMovementType.AfterInsertion));
        _snippet = session;
        document.Changed += OnSnippetDocumentChanged;
        SelectSnippetField(0);
    }

    private void SelectSnippetField(int position)
    {
        if (_snippet is not { } session)
            return;
        session.Position = position;
        var field = session.Fields.First(f => f.Index == session.CurrentIndex);
        var area = CodeEditor.TextArea;
        _movingInSnippet = true;
        try
        {
            area.ClearSelection();
            area.Caret.Offset = field.End.Offset;
            if (field.End.Offset > field.Start.Offset)
                area.Selection = Selection.Create(area, field.Start.Offset, field.End.Offset);
        }
        finally
        {
            _movingInSnippet = false;
        }
        // O $0 é o fim: dali em diante, o Tab volta a ser o de sempre.
        if (session.CurrentIndex == 0)
            EndSnippet();
    }

    // true quando o Tab andou entre os campos. false: ele segue como Tab comum.
    private bool MoveInSnippet(int direction)
    {
        if (_snippet is not { } session)
            return false;
        var caret = CodeEditor.TextArea.Caret.Offset;
        if (session.Document != CodeEditor.Document || caret < session.Begin.Offset || caret > session.Finish.Offset)
        {
            EndSnippet();
            return false;
        }

        var next = session.Position + direction;
        if (next < 0)
            return true;
        if (next >= session.Order.Count)
        {
            EndSnippet();
            return false;
        }
        SelectSnippetField(next);
        return true;
    }

    public void EndSnippet()
    {
        if (_snippet is not { } session)
            return;
        session.Document.Changed -= OnSnippetDocumentChanged;
        _snippet = null;
    }

    // Sair do trecho com o cursor encerra os campos, como clicar em outra linha.
    private void OnCaretMovedForSnippet()
    {
        if (_movingInSnippet || _snippet is not { } session)
            return;
        var caret = CodeEditor.TextArea.Caret.Offset;
        if (session.Document != CodeEditor.Document || caret < session.Begin.Offset || caret > session.Finish.Offset)
            EndSnippet();
    }

    // O que se digita num campo repetido aparece nos outros, como o $i de um for.
    private void OnSnippetDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_mirroring || _snippet is not { } session)
            return;
        var copies = session.Fields.Where(f => f.Index == session.CurrentIndex).ToList();
        if (copies.Count < 2)
            return;
        var primary = copies[0];
        if (e.Offset < primary.Start.Offset || e.Offset > primary.End.Offset)
            return;
        // Mudar o texto durante o aviso de mudança confundiria o editor: espelha logo em seguida.
        Dispatcher.UIThread.Post(() =>
        {
            if (_snippet != session || primary.Start.IsDeleted || primary.End.IsDeleted)
                return;
            var text = session.Document.GetText(primary.Start.Offset, primary.End.Offset - primary.Start.Offset);
            _mirroring = true;
            try
            {
                // De trás para frente: trocar um não muda a posição dos que vêm antes.
                foreach (var copy in copies.Skip(1).OrderByDescending(c => c.Start.Offset))
                {
                    var length = copy.End.Offset - copy.Start.Offset;
                    if (session.Document.GetText(copy.Start.Offset, length) != text)
                        session.Document.Replace(copy.Start.Offset, length, text);
                }
            }
            finally
            {
                _mirroring = false;
            }
        }, DispatcherPriority.Send);
    }
}
