namespace Sirius.Core;

// Ferramentas instaladas com a Sirius aberta ainda não estão no PATH deste processo.
// Acrescentar as pastas conhecidas faz o terminal e os servidores as encontrarem na hora.
public static class ToolPaths
{
    public static IEnumerable<string> Folders
    {
        get
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(programFiles, "dotnet");
            yield return Path.Combine(programFiles, "nodejs");
            yield return Path.Combine(programFiles, "Git", "cmd");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            // O XAMPP traz o PHP, mas não o coloca no PATH.
            yield return @"C:\xampp\php";
        }
    }

    public static void EnsureOnPath()
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var updated = WithTools(current);
        if (updated != current)
            Environment.SetEnvironmentVariable("PATH", updated);
    }

    public static string WithTools(string current)
    {
        var known = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => folder.Trim().TrimEnd(Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = Folders.Where(folder => !known.Contains(folder) && Directory.Exists(folder));
        return string.Join(Path.PathSeparator, missing.Prepend(current));
    }
}
