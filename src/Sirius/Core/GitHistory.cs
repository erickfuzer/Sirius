namespace Sirius.Core;

// Em um merge, o commit anterior é o primeiro pai: a comparação mostra o que entrou no branch.
public sealed record GitCommit(string Oid, string? FirstParent, string Author, DateTimeOffset Date, string Subject, string Message)
{
    public string ShortOid => Oid[..Math.Min(7, Oid.Length)];
}

public static class GitHistory
{
    // Cada campo termina em NUL, porque a mensagem do commit pode ter qualquer texto.
    public const string LogFormat = "%H%x00%P%x00%an%x00%at%x00%s%x00%B%x00";

    // Um commit estranho não pode travar a lista desenhando um assunto enorme.
    private const int MaxSubject = 300;
    private const int MaxMessage = 2000;
    private const int Fields = 6;

    public static List<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();
        var parts = output.Split('\0');
        for (var i = 0; i + Fields <= parts.Length; i += Fields)
        {
            var oid = parts[i].Trim();
            if (oid.Length == 0 || !long.TryParse(parts[i + 3], out var seconds))
                continue;

            commits.Add(new GitCommit(
                oid,
                parts[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                parts[i + 2],
                DateTimeOffset.FromUnixTimeSeconds(seconds),
                Clip(parts[i + 4], MaxSubject),
                Clip(parts[i + 5].Trim(), MaxMessage)));
        }
        return commits;
    }

    // Lê a saída de "git diff-tree --name-status -z".
    public static List<GitChange> ParseChanges(string output)
    {
        var changes = new List<GitChange>();
        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;

            var letter = parts[i][0];
            if (letter is 'R' or 'C')
            {
                if (i + 2 >= parts.Length)
                    break;
                changes.Add(letter == 'R'
                    ? new GitChange(parts[i + 2], parts[i + 1], GitChangeKind.Renamed, false)
                    : new GitChange(parts[i + 2], null, GitChangeKind.Added, false));
                i += 2;
            }
            else
            {
                if (i + 1 >= parts.Length)
                    break;
                var kind = letter switch
                {
                    'A' => GitChangeKind.Added,
                    'D' => GitChangeKind.Deleted,
                    _ => GitChangeKind.Modified,
                };
                changes.Add(new GitChange(parts[i + 1], null, kind, false));
                i++;
            }
        }
        return changes;
    }

    public static string RelativeDate(DateTimeOffset date, DateTimeOffset now)
    {
        var elapsed = now - date;
        // Relógios diferentes podem pôr um commit no futuro: a data certa é melhor que um "há -3 h".
        if (elapsed < TimeSpan.FromMinutes(-5))
            return FullDate(date, now);
        if (elapsed < TimeSpan.FromMinutes(1))
            return "agora";
        if (elapsed < TimeSpan.FromHours(1))
            return $"há {(int)elapsed.TotalMinutes} min";

        var days = (now.Date - date.ToOffset(now.Offset).Date).Days;
        if (days == 0)
            return $"há {(int)elapsed.TotalHours} h";
        if (days == 1)
            return "ontem";
        return days <= 30 ? $"há {days} dias" : FullDate(date, now);
    }

    public static string FullDate(DateTimeOffset date, DateTimeOffset now) =>
        date.ToOffset(now.Offset).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";
}
