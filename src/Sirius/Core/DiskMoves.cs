namespace Sirius.Core;

// Arquivos que outro programa renomeou ou moveu, para as abas seguirem o nome novo em vez de dizer que o arquivo sumiu.
public static class DiskMoves
{
    // Aplica as renomeações na ordem em que chegaram. Uma pasta renomeada leva junto tudo o que está dentro dela.
    public static string Apply(string path, IReadOnlyList<(string Old, string New)> renames)
    {
        foreach (var (old, renamed) in renames)
        {
            if (FileOperations.IsInside(path, old))
                path = renamed + path[old.Length..];
        }
        return path;
    }

    // O novo caminho de um arquivo aberto, ou null se ele continua onde estava ou não dá para saber para onde foi.
    public static string? Follow(string path, DateTime lastWriteUtc, IReadOnlyList<(string Old, string New)> renames, IReadOnlyList<string> created)
    {
        var renamed = Apply(path, renames);
        if (!string.Equals(renamed, path, StringComparison.Ordinal) && File.Exists(renamed))
        {
            // Quem salva por um arquivo temporário renomeia o original e põe outro no lugar: aí a aba fica onde está.
            // Só a troca de maiúsculas mantém o nome antigo existindo e ainda assim é uma renomeação.
            if (!File.Exists(path) || string.Equals(renamed, path, StringComparison.OrdinalIgnoreCase))
                return renamed;
        }
        if (File.Exists(path))
            return null;

        // Mover pelo Explorer chega como apagar e criar, e mover mantém a data da última gravação.
        string? found = null;
        foreach (var target in created)
        {
            foreach (var candidate in Candidates(path, target))
            {
                if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)
                    || File.GetLastWriteTimeUtc(candidate) != lastWriteUtc)
                    continue;
                if (found is not null && !string.Equals(found, candidate, StringComparison.OrdinalIgnoreCase))
                    return null;
                found = candidate;
            }
        }
        return found;
    }

    // O arquivo ou uma das pastas acima dele com o mesmo nome do que foi criado: "c:\p\a\b\x.php" e a pasta
    // criada "c:\p\z\b" dão "c:\p\z\b\x.php".
    private static IEnumerable<string> Candidates(string path, string created)
    {
        var name = Path.GetFileName(created);
        if (name.Length == 0)
            yield break;
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (string.Equals(Path.GetFileName(current), name, StringComparison.OrdinalIgnoreCase))
                yield return created + path[current.Length..];
        }
    }
}
