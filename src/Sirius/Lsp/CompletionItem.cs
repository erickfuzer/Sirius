using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Sirius.Lsp;

// Uma linha da lista de sugestões.
public sealed class CompletionItem(CompletionEntry entry) : ICompletionData
{
    public IImage? Image => null;
    public string Text => entry.Label;
    public object Content => entry.Label;
    public object? Description =>
        string.IsNullOrWhiteSpace(entry.Detail) ? entry.Documentation : string.IsNullOrWhiteSpace(entry.Documentation) ? entry.Detail : $"{entry.Detail}\n\n{entry.Documentation}";
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, entry.InsertText);
}
