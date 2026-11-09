namespace Sirius.Core;

// Um arquivo renomeado ou movido fora do Git, como no Explorer, aparece para ele como um apagado e um novo, até
// os dois serem preparados juntos. Aqui eles viram uma renomeação quando o conteúdo é o mesmo ou quase, como o
// Git faz ao preparar: metade das linhas em comum basta.
public static class GitRenames
{
    public const double MinSimilarity = 0.5;
    public const int MaxDeleted = 30;
    public const int MaxAdded = 300;
    public const long MaxBytes = 1_000_000;

    // Comparar linha a linha custa: só entre arquivos de mesma extensão, e até este tanto de pares.
    private const int MaxComparisons = 400;

    public static List<(string Deleted, string Added)> Pair(
        IReadOnlyList<(string Path, byte[] Content)> deleted, IReadOnlyList<(string Path, byte[] Content)> added)
    {
        var pairs = new List<(string, string)>();
        var usedDeleted = new HashSet<string>(StringComparer.Ordinal);
        var usedAdded = new HashSet<string>(StringComparer.Ordinal);
        var addedTexts = added.Where(file => file.Content.Length > 0).Select(file => (file.Path, Text: Normalized(file.Content))).ToList();
        var deletedTexts = deleted.Where(file => file.Content.Length > 0).Select(file => (file.Path, Text: Normalized(file.Content))).ToList();

        // Primeiro o conteúdo idêntico, com preferência pelo mesmo nome, que é mover sem renomear.
        foreach (var (path, text) in deletedTexts)
        {
            var same = addedTexts.Where(file => !usedAdded.Contains(file.Path) && file.Text == text)
                .OrderBy(file => SameName(path, file.Path) ? 0 : 1).Select(file => file.Path).FirstOrDefault();
            if (same is null)
                continue;
            pairs.Add((path, same));
            usedDeleted.Add(path);
            usedAdded.Add(same);
        }

        // Depois o quase igual: renomeado e editado. O par mais parecido primeiro.
        var candidates = new List<(double Score, string Deleted, string Added)>();
        var comparisons = 0;
        foreach (var (path, text) in deletedTexts.Where(file => !usedDeleted.Contains(file.Path) && !IsBinary(file.Text)))
        {
            foreach (var file in addedTexts.Where(file => !usedAdded.Contains(file.Path) && !IsBinary(file.Text)
                && string.Equals(Path.GetExtension(file.Path), Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)))
            {
                if (++comparisons > MaxComparisons)
                    break;
                var score = Similarity(text, file.Text);
                if (score >= MinSimilarity)
                    candidates.Add((score + (SameName(path, file.Path) ? 0.001 : 0), path, file.Path));
            }
        }
        foreach (var (_, from, to) in candidates.OrderByDescending(candidate => candidate.Score))
        {
            if (usedDeleted.Contains(from) || usedAdded.Contains(to))
                continue;
            pairs.Add((from, to));
            usedDeleted.Add(from);
            usedAdded.Add(to);
        }
        return pairs;
    }

    // Quantas linhas os dois têm em comum, de 0 a 1.
    public static double Similarity(string a, string b)
    {
        var left = LineDiff.SplitLines(a);
        var right = LineDiff.SplitLines(b);
        var equal = LineDiff.Compare(left, right).Where(op => op.Kind == DiffKind.Equal).Sum(op => op.ACount);
        return 2.0 * equal / (left.Length + right.Length);
    }

    private static bool SameName(string a, string b) =>
        string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsBinary(string text) => text.Contains('\0');

    // O Git costuma guardar com \n, e o Windows grava com \r\n: os dois contam como iguais.
    private static string Normalized(byte[] content) =>
        System.Text.Encoding.Latin1.GetString(content).Replace("\r\n", "\n");
}
