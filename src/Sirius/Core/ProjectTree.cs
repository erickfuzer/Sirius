namespace Sirius.Core;

public sealed record TreeEntry(string Name, string FullPath, bool IsFolder);

public static class ProjectTree
{
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "__pycache__",
    };

    public static List<TreeEntry> List(string folder)
    {
        var entries = new List<TreeEntry>();
        try
        {
            var directory = new DirectoryInfo(folder);
            entries.AddRange(directory.EnumerateDirectories()
                .Where(d => !IgnoredFolders.Contains(d.Name))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new TreeEntry(d.Name, d.FullName, true)));
            entries.AddRange(directory.EnumerateFiles()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new TreeEntry(f.Name, f.FullName, false)));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Pastas sem permissão aparecem vazias em vez de derrubar a IDE.
        }
        return entries;
    }

    public static List<string> FindFiles(string root, int limit = 20000)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && found.Count < limit)
        {
            foreach (var entry in List(pending.Pop()))
            {
                if (entry.IsFolder)
                    pending.Push(entry.FullPath);
                else if (found.Count < limit)
                    found.Add(entry.FullPath);
            }
        }
        return found;
    }

    public static string? CurrentBranch(string root)
    {
        try
        {
            var head = Path.Combine(root, ".git", "HEAD");
            if (!File.Exists(head))
                return null;
            var text = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text[..Math.Min(7, text.Length)];
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
