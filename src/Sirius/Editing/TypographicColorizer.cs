using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius.Editing;

// A assinatura da Sirius: o código é quase monocromático e o destaque vem do peso da fonte.
public sealed class TypographicColorizer : DocumentColorizingTransformer
{
    private static readonly FontFamily Mono = new("avares://Sirius/Assets/Fonts#Geist Mono");
    private static readonly FontFamily Serif = new("avares://Sirius/Assets/Fonts#Newsreader");

    public SyntaxHighlighter? Highlighter { get; set; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (Highlighter is null)
            return;

        var palette = Palette.Current;
        foreach (var token in Highlighter.TokensFor(line.LineNumber))
        {
            var start = line.Offset + token.Start;
            var end = Math.Min(start + token.Length, line.EndOffset);
            if (start >= end)
                continue;

            ChangeLinePart(start, end, element =>
            {
                var run = element.TextRunProperties;
                switch (token.Kind)
                {
                    case TokenKind.Keyword:
                        run.SetTypeface(new Typeface(Mono, FontStyle.Normal, FontWeight.SemiBold));
                        break;
                    case TokenKind.Name:
                        run.SetTypeface(new Typeface(Mono, FontStyle.Normal, FontWeight.Medium));
                        break;
                    case TokenKind.Literal:
                        run.SetForegroundBrush(palette.String);
                        break;
                    case TokenKind.Comment:
                        run.SetForegroundBrush(palette.Muted);
                        run.SetTypeface(new Typeface(Serif, FontStyle.Italic, FontWeight.Normal));
                        break;
                    case TokenKind.Punctuation:
                        run.SetForegroundBrush(palette.Muted);
                        break;
                }
            });
        }
    }
}
