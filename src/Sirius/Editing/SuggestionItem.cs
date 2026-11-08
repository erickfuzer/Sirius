using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Sirius.Theming;

namespace Sirius.Editing;

// Uma linha da lista de sugestões que não vem do servidor: uma abreviação do Emmet ou um atalho da
// linguagem. Filter é o que a lista compara com o que foi digitado; Label é o que aparece; Kind vai
// à direita, como "Emmet"; Preview é o texto que vai entrar, mostrado ao lado ao parar no item.
public sealed class SuggestionItem(string filter, string label, string kind, string preview, Action accept) : ICompletionData
{
    private const int PreviewLines = 14;
    private const double MinRowWidth = 220;
    private const double PreviewWidth = 420;

    public IImage? Image => null;
    public string Text => filter;
    public string Label => label;
    public string Kind => kind;
    public double Priority => 1;

    public object Content => new DockPanel
    {
        // Largura mínima para o tipo ir para a direita, como no VS Code, mesmo numa lista curta.
        MinWidth = MinRowWidth,
        Children =
        {
            new TextBlock { Text = kind, FontSize = 11, Foreground = Palette.Current.Muted, Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right },
            new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
        },
    };

    public object Description
    {
        get
        {
            var lines = preview.Replace("\r", "").Split('\n');
            var text = string.Join('\n', lines.Take(PreviewLines));
            if (lines.Length > PreviewLines)
                text += "\n…";
            var block = new TextBlock { Text = text, FontSize = 12, MaxWidth = PreviewWidth, TextWrapping = TextWrapping.Wrap };
            if (Application.Current?.TryFindResource("CodeFont", out var font) == true && font is FontFamily family)
                block.FontFamily = family;
            return block;
        }
    }

    // O texto ainda não muda aqui: quem insere é a janela, logo depois de a lista fechar.
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => accept();
}
