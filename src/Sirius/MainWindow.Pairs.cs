using Avalonia.Input;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius;

// Aspas, parênteses e tags que se fecham sozinhos enquanto se digita.
public partial class MainWindow
{
    // Só o que a Sirius acrescentou pula quando digitado de novo e some junto no Backspace.
    // Assim, o ")" que a própria pessoa escreveu continua sendo dela.
    private readonly List<TextAnchor> _pairClosers = [];
    private TextDocument? _pairDocument;
    private int _pairLine;

    private void OnPairTextEntering(object? sender, TextInputEventArgs e)
    {
        var area = CodeEditor.TextArea;
        if (e.Text is not { Length: 1 } typed || ActiveDocument is not { IsImage: false } document
            || CodeEditor.IsReadOnly || !_settings.AutoClosePairs || !AutoPairs.Supports(document.FullPath))
            return;

        var c = typed[0];
        var text = document.Document;
        var caret = area.Caret.Offset;
        var closer = AutoPairs.CloserFor(c);

        if (!area.Selection.IsEmpty)
        {
            if (closer is { } wrapper && area.Selection is SimpleSelection)
            {
                WrapSelection(text, c, wrapper);
                e.Handled = true;
            }
            return;
        }

        if (TakeAddedCloser(text, caret, c))
        {
            CloseCompletion();
            area.Caret.Offset = caret + 1;
            e.Handled = true;
            return;
        }

        if (closer is not { } pair || !AutoPairs.ShouldClose(text.Text, caret, c))
            return;

        CloseCompletion();
        text.Insert(caret, $"{c}{pair}");
        area.Caret.Offset = caret + 1;
        RememberCloser(text, caret + 1);
        e.Handled = true;
        // A digitação foi tratada aqui, e o evento de sempre não vai acontecer.
        SuggestAfterTyping(c);
    }

    private void WrapSelection(TextDocument text, char opener, char closer)
    {
        var area = CodeEditor.TextArea;
        var segment = area.Selection.SurroundingSegment;
        var (start, end) = (segment.Offset, segment.EndOffset);
        using (text.RunUpdate())
        {
            text.Insert(end, closer.ToString());
            text.Insert(start, opener.ToString());
        }
        area.Selection = Selection.Create(area, start + 1, end + 1);
        area.Caret.Offset = end + 1;
    }

    private void RememberCloser(TextDocument text, int offset)
    {
        var anchor = text.CreateAnchor(offset);
        // Digitar colado nele, por dentro do par, precisa empurrá-lo para a frente.
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        _pairClosers.Add(anchor);
        _pairDocument = text;
        _pairLine = CodeEditor.TextArea.Caret.Line;
    }

    private bool HasAddedCloser(TextDocument text, int offset, char c) =>
        _pairDocument == text && offset < text.TextLength && text.GetCharAt(offset) == c
        && _pairClosers.Any(anchor => !anchor.IsDeleted && anchor.Offset == offset);

    private bool TakeAddedCloser(TextDocument text, int offset, char c)
    {
        if (!HasAddedCloser(text, offset, c))
            return false;
        _pairClosers.RemoveAll(anchor => anchor.IsDeleted || anchor.Offset == offset);
        return true;
    }

    private void ForgetPairsOnOtherLine()
    {
        if (_pairClosers.Count > 0 && (CodeEditor.TextArea.Document != _pairDocument || CodeEditor.TextArea.Caret.Line != _pairLine))
            _pairClosers.Clear();
    }

    private bool TryDeleteEmptyPair()
    {
        var area = CodeEditor.TextArea;
        if (!area.IsKeyboardFocusWithin || !area.Selection.IsEmpty || CodeEditor.IsReadOnly || ActiveDocument is not { } document)
            return false;

        var text = document.Document;
        var caret = area.Caret.Offset;
        if (caret == 0 || AutoPairs.CloserFor(text.GetCharAt(caret - 1)) is not { } closer || !TakeAddedCloser(text, caret, closer))
            return false;

        text.Remove(caret - 1, 2);
        return true;
    }

    private bool TryOpenPairOnEnter()
    {
        var area = CodeEditor.TextArea;
        if (!area.IsKeyboardFocusWithin || !area.Selection.IsEmpty || _completion is not null || CodeEditor.IsReadOnly
            || ActiveDocument is not { IsImage: false } document || !AutoPairs.Supports(document.FullPath))
            return false;

        var text = document.Document;
        var caret = area.Caret.Offset;
        if (!AutoPairs.OpensOnEnter(text.Text, caret))
            return false;

        var line = text.GetLineByOffset(caret);
        var indent = TextUtilities.GetLeadingWhitespace(text, line);
        var leading = text.GetText(indent);
        var newline = TextUtilities.GetNewLineFromDocument(text, line.LineNumber);
        var middle = newline + leading + CodeEditor.Options.IndentationString;

        text.Insert(caret, middle + newline + leading);
        area.Caret.Offset = caret + middle.Length;
        return true;
    }

    // Depois da digitação de sempre, porque o ">" precisa estar no texto e seguir abrindo a lista de sugestões.
    private void OnTagTextEntered(object? sender, TextInputEventArgs e)
    {
        var area = CodeEditor.TextArea;
        if (e.Text != ">" || ActiveDocument is not { IsImage: false } document || CodeEditor.IsReadOnly || !_settings.AutoClosePairs
            || !Emmet.Supports(document.FullPath))
            return;

        var text = document.Document;
        var caret = area.Caret.Offset;
        if (AutoPairs.TagToClose(text.Text, caret) is not { } closing)
            return;

        text.Insert(caret, closing);
        area.Caret.Offset = caret;
    }
}
