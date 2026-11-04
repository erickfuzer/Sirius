namespace Sirius.Core;

public enum DiffKind { Equal, Insert, Delete, Replace }

// Um trecho da comparação. AStart e BStart contam linhas a partir de zero.
public readonly record struct DiffOp(DiffKind Kind, int AStart, int ACount, int BStart, int BCount);

public enum GitLineKind { Added, Modified, DeletedAbove }

public enum DiffRowKind { Same, Changed, Filler }

// Uma linha de cada lado na comparação. Número nulo indica a linha vazia que mantém os lados alinhados.
public sealed record DiffRow(int? LeftNumber, string LeftText, DiffRowKind LeftKind, int? RightNumber, string RightText, DiffRowKind RightKind);

// Compara dois textos linha a linha, pelo algoritmo de Myers.
public static class LineDiff
{
    // Acima disso a comparação custaria caro demais: o trecho restante vira uma troca inteira.
    private const int MaxEditDistance = 2000;

    public static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r'))
                continue;
            lines.Add(text[start..i]);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        lines.Add(text[start..]);
        return lines.ToArray();
    }

    public static List<DiffOp> Compare(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
            suffix++;

        var ops = new List<DiffOp>();
        if (prefix > 0)
            ops.Add(new DiffOp(DiffKind.Equal, 0, prefix, 0, prefix));

        var n = a.Count - prefix - suffix;
        var m = b.Count - prefix - suffix;
        if (n > 0 || m > 0)
            AddMiddle(ops, a, b, prefix, n, m);

        if (suffix > 0)
            ops.Add(new DiffOp(DiffKind.Equal, a.Count - suffix, suffix, b.Count - suffix, suffix));
        return ops;
    }

    private static void AddMiddle(List<DiffOp> ops, IReadOnlyList<string> a, IReadOnlyList<string> b, int offset, int n, int m)
    {
        // Comparar números é muito mais rápido do que comparar textos a cada passo.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var x = new int[n];
        var y = new int[m];
        for (var i = 0; i < n; i++)
            x[i] = IdOf(ids, a[offset + i]);
        for (var i = 0; i < m; i++)
            y[i] = IdOf(ids, b[offset + i]);

        var path = ShortestEdit(x, y);
        if (path is null)
        {
            ops.Add(MakeChange(offset, n, offset, m));
            return;
        }

        // Agrupa os passos: remoções e inserções vizinhas formam uma troca.
        var aIndex = 0;
        var bIndex = 0;
        var position = 0;
        while (position < path.Count)
        {
            var deleted = 0;
            var inserted = 0;
            while (position < path.Count && path[position] != Step.Equal)
            {
                if (path[position] == Step.Delete)
                    deleted++;
                else
                    inserted++;
                position++;
            }
            if (deleted > 0 || inserted > 0)
            {
                ops.Add(MakeChange(offset + aIndex, deleted, offset + bIndex, inserted));
                aIndex += deleted;
                bIndex += inserted;
            }

            var equal = 0;
            while (position < path.Count && path[position] == Step.Equal)
            {
                equal++;
                position++;
            }
            if (equal > 0)
            {
                ops.Add(new DiffOp(DiffKind.Equal, offset + aIndex, equal, offset + bIndex, equal));
                aIndex += equal;
                bIndex += equal;
            }
        }
    }

    private static DiffOp MakeChange(int aStart, int aCount, int bStart, int bCount)
    {
        var kind = aCount == 0 ? DiffKind.Insert : bCount == 0 ? DiffKind.Delete : DiffKind.Replace;
        return new DiffOp(kind, aStart, aCount, bStart, bCount);
    }

    private static int IdOf(Dictionary<string, int> ids, string line)
    {
        if (!ids.TryGetValue(line, out var id))
        {
            id = ids.Count;
            ids[line] = id;
        }
        return id;
    }

    private enum Step : byte { Equal, Delete, Insert }

    private static List<Step>? ShortestEdit(int[] a, int[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var limit = Math.Min(n + m, MaxEditDistance);
        var offset = limit + 1;
        var v = new int[2 * limit + 3];
        var trace = new List<int[]>();

        for (var d = 0; d <= limit; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                    return Backtrack(trace, offset, n, m);
            }
        }
        return null;
    }

    private static List<Step> Backtrack(List<int[]> trace, int offset, int n, int m)
    {
        var steps = new List<Step>(n + m);
        var x = n;
        var y = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var v = trace[d];
            var k = x - y;
            var previousK = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? k + 1 : k - 1;
            var previousX = v[offset + previousK];
            var previousY = previousX - previousK;

            while (x > previousX && y > previousY)
            {
                steps.Add(Step.Equal);
                x--;
                y--;
            }
            if (d > 0)
                steps.Add(x == previousX ? Step.Insert : Step.Delete);
            x = previousX;
            y = previousY;
        }
        steps.Reverse();
        return steps;
    }

    // As marcas da margem: a chave é o número da linha atual, a partir de 1.
    public static Dictionary<int, GitLineKind> LineMarks(IEnumerable<DiffOp> ops, int lineCount)
    {
        var marks = new Dictionary<int, GitLineKind>();
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case DiffKind.Insert:
                    for (var i = 0; i < op.BCount; i++)
                        marks[op.BStart + i + 1] = GitLineKind.Added;
                    break;
                case DiffKind.Replace:
                    for (var i = 0; i < op.BCount; i++)
                        marks[op.BStart + i + 1] = GitLineKind.Modified;
                    break;
                case DiffKind.Delete:
                    marks.TryAdd(Math.Clamp(op.BStart + 1, 1, Math.Max(lineCount, 1)), GitLineKind.DeletedAbove);
                    break;
            }
        }
        return marks;
    }

    // As linhas dos dois lados, alinhadas para a comparação lado a lado.
    public static List<DiffRow> Align(IReadOnlyList<string> a, IReadOnlyList<string> b, IEnumerable<DiffOp> ops)
    {
        var rows = new List<DiffRow>();
        foreach (var op in ops)
        {
            if (op.Kind == DiffKind.Equal)
            {
                for (var i = 0; i < op.ACount; i++)
                    rows.Add(new DiffRow(op.AStart + i + 1, a[op.AStart + i], DiffRowKind.Same, op.BStart + i + 1, b[op.BStart + i], DiffRowKind.Same));
                continue;
            }

            for (var i = 0; i < Math.Max(op.ACount, op.BCount); i++)
            {
                var hasLeft = i < op.ACount;
                var hasRight = i < op.BCount;
                rows.Add(new DiffRow(
                    hasLeft ? op.AStart + i + 1 : null, hasLeft ? a[op.AStart + i] : "", hasLeft ? DiffRowKind.Changed : DiffRowKind.Filler,
                    hasRight ? op.BStart + i + 1 : null, hasRight ? b[op.BStart + i] : "", hasRight ? DiffRowKind.Changed : DiffRowKind.Filler));
            }
        }
        return rows;
    }
}
