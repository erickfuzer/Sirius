using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Sirius.Lsp;

// Uma linha da lista de sugestões.
public sealed class CompletionItem(CompletionEntry entry, int? editStart, int charactersAfterCaret, Action? suggestAgain) : ICompletionData
{
    private const double DescriptionWidth = 420;

    public IImage? Image => null;
    public string Text => entry.Label;
    public object Content => entry.Label;
    public double Priority => 0;

    public string? DescriptionText =>
        string.IsNullOrWhiteSpace(entry.Detail) ? entry.Documentation : string.IsNullOrWhiteSpace(entry.Documentation) ? entry.Detail : $"{entry.Detail}\n\n{entry.Documentation}";

    // Com largura limitada: sem isso, a janela da descrição cresce até o tamanho do texto.
    public object? Description => string.IsNullOrWhiteSpace(DescriptionText)
        ? null
        : new TextBlock { Text = DescriptionText, TextWrapping = TextWrapping.Wrap, MaxWidth = DescriptionWidth, FontSize = 12 };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var document = textArea.Document;
        var start = editStart ?? completionSegment.Offset;
        var end = Math.Min(document.TextLength, completionSegment.EndOffset + charactersAfterCaret);

        document.Replace(start, end - start, entry.InsertText);
        if (entry.CaretInInsertText is { } caret)
            textArea.Caret.Offset = start + caret;
        if (entry.SuggestAgain)
            suggestAgain?.Invoke();
    }
}
