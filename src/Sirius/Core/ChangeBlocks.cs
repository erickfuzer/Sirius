namespace Sirius.Core;

// Um trecho que mudou desde o último commit. First e Last contam as linhas do texto de agora a partir de 1.
// Count 0 é um trecho apagado: as linhas sumiram logo acima de First, e a marca fica em MarkLine.
// OldFirst é onde as linhas antigas começavam no último commit.
public sealed record ChangeBlock(int First, int Count, IReadOnlyList<string> OldLines, int MarkLine, int OldFirst = 1)
{
    public bool IsAdded => OldLines.Count == 0;
    public bool IsDeleted => Count == 0;
    public int Last => Count == 0 ? MarkLine : First + Count - 1;

    // Se as linhas de from a to, inclusive, pegam este trecho. Um trecho apagado conta pela linha da marca.
    public bool Touches(int from, int to) => IsDeleted ? MarkLine >= from && MarkLine <= to : First <= to && Last >= from;
}

// Os trechos alterados de um arquivo, para ver como eram antes e desfazer só alguns, como no VS Code.
public static class ChangeBlocks
{
    public static List<ChangeBlock> Find(string baseText, string currentText)
    {
        var a = LineDiff.SplitLines(baseText);
        var b = LineDiff.SplitLines(currentText);
        return FromOps(a, b, LineDiff.Compare(a, b));
    }

    public static List<ChangeBlock> FromOps(IReadOnlyList<string> a, IReadOnlyList<string> b, IEnumerable<DiffOp> ops)
    {
        var blocks = new List<ChangeBlock>();
        foreach (var op in ops)
        {
            if (op.Kind == DiffKind.Equal)
                continue;
            var old = a.Skip(op.AStart).Take(op.ACount).ToArray();
            // A mesma linha em que LineMarks desenha a marca.
            var mark = op.BCount == 0 ? Math.Clamp(op.BStart + 1, 1, Math.Max(b.Count, 1)) : op.BStart + 1;
            blocks.Add(new ChangeBlock(op.BStart + 1, op.BCount, old, mark, op.AStart + 1));
        }
        return blocks;
    }

    // O trecho da marca nesta linha, ou -1.
    public static int IndexAt(IReadOnlyList<ChangeBlock> blocks, int line)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!blocks[i].IsDeleted && blocks[i].Touches(line, line))
                return i;
        }
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].IsDeleted && blocks[i].MarkLine == line)
                return i;
        }
        return -1;
    }

    // O texto com os trechos escolhidos de volta como estavam no último commit. Só eles mudam, e cada um
    // usa a quebra de linha que o arquivo já usa.
    public static string Revert(string currentText, IEnumerable<ChangeBlock> blocks)
    {
        var (starts, ends) = LineBounds(currentText);
        var lineCount = starts.Count;
        var newline = NewlineOf(currentText);
        var text = new System.Text.StringBuilder(currentText);

        // De baixo para cima, para as posições de cima continuarem valendo.
        foreach (var block in blocks.OrderByDescending(block => block.First).ThenByDescending(block => block.Count))
        {
            var old = string.Join(newline, block.OldLines);
            if (block.IsDeleted)
            {
                if (block.First <= lineCount)
                    text.Insert(starts[block.First - 1], old + newline);
                else
                    text.Append(newline + old);
            }
            else if (block.IsAdded)
            {
                int from, to;
                if (block.Last < lineCount)
                    (from, to) = (starts[block.First - 1], starts[block.Last]);
                else if (block.First > 1)
                    (from, to) = (ends[block.First - 2], ends[block.Last - 1]);
                else
                    (from, to) = (0, ends[block.Last - 1]);
                text.Remove(from, to - from);
            }
            else
            {
                var from = starts[block.First - 1];
                var to = ends[block.Last - 1];
                text.Remove(from, to - from).Insert(from, old);
            }
        }
        return text.ToString();
    }

    // Onde começa cada linha e onde termina o texto dela, sem a quebra. As mesmas linhas de LineDiff.SplitLines.
    private static (List<int> Starts, List<int> Ends) LineBounds(string text)
    {
        var starts = new List<int> { 0 };
        var ends = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r'))
                continue;
            ends.Add(i);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            starts.Add(i + 1);
        }
        ends.Add(text.Length);
        return (starts, ends);
    }

    private static string NewlineOf(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        if (index < 0 || text[index] == '\n')
            return "\n";
        return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
    }
}
