using System.Text;
using System.Text.RegularExpressions;

namespace Sirius.Core;

public enum ConflictChoice { Mine, Received, Both }

// Onde fica um conflito nos dois lados montados: linha inicial (a partir de 0) e quantas linhas.
public sealed record ConflictBlock(int MineStart, int MineCount, int ReceivedStart, int ReceivedCount);

// Os dois lados inteiros, com o resto do arquivo igual nos dois.
public sealed record ConflictSides(string Mine, string Received, IReadOnlyList<ConflictBlock> Blocks)
{
    public int Count => Blocks.Count;
}

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

    // Lê o arquivo uma vez só: o texto de fora vai para "outside", e cada conflito, inteiro, para "conflict".
    // Devolve false se alguma marca está solta.
    private static bool Walk(string text, Action<string> outside, Action<List<string>, List<string>, string> conflict)
    {
        var part = Part.Outside;
        var mine = new List<string>();
        var received = new List<string>();
        var raw = new StringBuilder();
        foreach (var line in Lines(text))
        {
            if (part != Part.Outside)
                raw.Append(line);
            switch (part)
            {
                case Part.Outside when IsMarker(line, '<'):
                    part = Part.Mine;
                    mine.Clear();
                    received.Clear();
                    raw.Clear().Append(line);
                    continue;
                case Part.Outside when IsMarker(line, '=') || IsMarker(line, '>') || IsMarker(line, '|'):
                    return false;
                case Part.Outside:
                    outside(line);
                    continue;
                case Part.Mine when IsMarker(line, '|'):
                    part = Part.Base;
                    continue;
                case Part.Mine or Part.Base when IsMarker(line, '='):
                    part = Part.Received;
                    continue;
                case Part.Received when IsMarker(line, '>'):
                    part = Part.Outside;
                    conflict(mine, received, raw.ToString());
                    continue;
                case Part.Mine or Part.Base or Part.Received when IsMarker(line, '<'):
                    return false;
                case Part.Mine:
                    mine.Add(line);
                    continue;
                case Part.Received:
                    received.Add(line);
                    continue;
            }
        }
        return part == Part.Outside;
    }

    // Null se não há conflito legível.
    public static ConflictSides? Sides(string text)
    {
        var mine = new StringBuilder();
        var received = new StringBuilder();
        var blocks = new List<ConflictBlock>();
        var mineLines = 0;
        var receivedLines = 0;
        var ok = Walk(text,
            line =>
            {
                mine.Append(line);
                received.Append(line);
                mineLines++;
                receivedLines++;
            },
            (mineBlock, receivedBlock, _) =>
            {
                blocks.Add(new ConflictBlock(mineLines, mineBlock.Count, receivedLines, receivedBlock.Count));
                mineBlock.ForEach(line => mine.Append(line));
                receivedBlock.ForEach(line => received.Append(line));
                mineLines += mineBlock.Count;
                receivedLines += receivedBlock.Count;
            });
        return ok && blocks.Count > 0 ? new ConflictSides(mine.ToString(), received.ToString(), blocks) : null;
    }

    // O arquivo com o lado escolhido no lugar das marcas. Sem "only", em todos os conflitos; com ele, só
    // naquele (contado a partir de 0), e os outros ficam como estão. "Os dois" põe o seu e depois o recebido,
    // como o "Accept Both" do VS Code.
    public static string? Resolve(string text, ConflictChoice choice, int? only = null)
    {
        var result = new StringBuilder();
        var index = 0;
        var found = false;
        var ok = Walk(text,
            line => result.Append(line),
            (mine, received, raw) =>
            {
                if (only is { } wanted && wanted != index++)
                {
                    result.Append(raw);
                    return;
                }
                found = true;
                if (choice is ConflictChoice.Mine or ConflictChoice.Both)
                    mine.ForEach(line => result.Append(line));
                if (choice is ConflictChoice.Received or ConflictChoice.Both)
                    received.ForEach(line => result.Append(line));
            });
        return ok && found ? result.ToString() : null;
    }
}
