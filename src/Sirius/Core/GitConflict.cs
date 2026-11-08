using System.Text;
using System.Text.RegularExpressions;

namespace Sirius.Core;

public enum ConflictChoice { Mine, Received, Both }

// As marcas que o Git deixa num arquivo em conflito, sem depender da janela:
//   <<<<<<< HEAD          o que estava neste computador
//   ||||||| base          (só com diff3) como era antes dos dois, que não entra em nenhum lado
//   =======
//   >>>>>>> origin/main   o que veio no Receber
// Um arquivo com marca solta, sem o fechamento, não é tratado: escolher às cegas apagaria texto.
public static class GitConflict
{
    private static readonly Regex Line = new(@"[^\r\n]*(\r\n|\r|\n)?", RegexOptions.CultureInvariant);

    private enum Part { Outside, Mine, Base, Received }

    private static bool IsMarker(string line, char c)
    {
        var content = line.TrimEnd('\r', '\n');
        return content.Length >= 7 && content.AsSpan(0, 7).IndexOfAnyExcept(c) < 0
            && (content.Length == 7 || content[7] == ' ' || (c == '=' && content[7..].Trim().Length == 0));
    }

    private static IEnumerable<string> Lines(string text)
    {
        foreach (Match match in Line.Matches(text))
        {
            if (match.Length > 0)
                yield return match.Value;
        }
    }

    // Os dois lados inteiros, com o resto do arquivo igual nos dois. Null se não há conflito legível.
    public static (string Mine, string Received, int Count)? Sides(string text)
    {
        var mine = new StringBuilder();
        var received = new StringBuilder();
        var part = Part.Outside;
        var count = 0;
        foreach (var line in Lines(text))
        {
            switch (part)
            {
                case Part.Outside when IsMarker(line, '<'):
                    part = Part.Mine;
                    count++;
                    continue;
                case Part.Outside when IsMarker(line, '=') || IsMarker(line, '>') || IsMarker(line, '|'):
                    return null;
                case Part.Outside:
                    mine.Append(line);
                    received.Append(line);
                    continue;
                case Part.Mine when IsMarker(line, '|'):
                    part = Part.Base;
                    continue;
                case Part.Mine or Part.Base when IsMarker(line, '='):
                    part = Part.Received;
                    continue;
                case Part.Received when IsMarker(line, '>'):
                    part = Part.Outside;
                    continue;
                case Part.Mine or Part.Base or Part.Received when IsMarker(line, '<'):
                    return null;
                case Part.Mine:
                    mine.Append(line);
                    continue;
                case Part.Base:
                    continue;
                case Part.Received:
                    received.Append(line);
                    continue;
            }
        }
        return part == Part.Outside && count > 0 ? (mine.ToString(), received.ToString(), count) : null;
    }

    // O arquivo sem as marcas, com o lado escolhido em cada conflito. "Os dois" põe o seu e depois o recebido,
    // como o "Accept Both" do VS Code.
    public static string? Resolve(string text, ConflictChoice choice)
    {
        if (Sides(text) is null)
            return null;
        var result = new StringBuilder();
        var part = Part.Outside;
        var mine = new StringBuilder();
        var received = new StringBuilder();
        foreach (var line in Lines(text))
        {
            if (part == Part.Outside && IsMarker(line, '<'))
            {
                part = Part.Mine;
                mine.Clear();
                received.Clear();
            }
            else if (part == Part.Mine && IsMarker(line, '|'))
                part = Part.Base;
            else if (part is Part.Mine or Part.Base && IsMarker(line, '='))
                part = Part.Received;
            else if (part == Part.Received && IsMarker(line, '>'))
            {
                part = Part.Outside;
                if (choice is ConflictChoice.Mine or ConflictChoice.Both)
                    result.Append(mine);
                if (choice is ConflictChoice.Received or ConflictChoice.Both)
                    result.Append(received);
            }
            else if (part == Part.Mine)
                mine.Append(line);
            else if (part == Part.Received)
                received.Append(line);
            else if (part == Part.Outside)
                result.Append(line);
        }
        return result.ToString();
    }
}
