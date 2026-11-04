using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// Descobre como executar um projeto olhando os arquivos dele.
public static partial class RunDetector
{
    [GeneratedRegex(@"<OutputType>\s*(Win)?Exe\s*</OutputType>", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutableProject();

    public static string? Detect(string root, string? activeFile)
    {
        return DotNet(root)
            ?? Node(root)
            ?? (File.Exists(Path.Combine(root, "Cargo.toml")) ? "cargo run" : null)
            ?? (File.Exists(Path.Combine(root, "go.mod")) ? "go run ." : null)
            ?? (File.Exists(Path.Combine(root, "main.py")) ? "python main.py" : null)
            ?? (File.Exists(Path.Combine(root, "Makefile")) ? "make" : null)
            ?? ActiveScript(activeFile);
    }

    private static string? DotNet(string root)
    {
        var projects = ProjectTree.FindFiles(root, 5000)
            .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => Depth(root, path) <= 3)
            .ToList();
        if (projects.Count == 0)
            return null;

        var executable = projects
            .Where(path => !Path.GetFileNameWithoutExtension(path).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(IsExecutable)
            .OrderBy(path => Depth(root, path))
            .FirstOrDefault();
        if (executable is not null)
            return $"dotnet run --project \"{Path.GetRelativePath(root, executable)}\"";

        return Directory.EnumerateFiles(root, "*.csproj").Any() ? "dotnet run" : null;
    }

    private static bool IsExecutable(string project)
    {
        try
        {
            return ExecutableProject().IsMatch(File.ReadAllText(project));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int Depth(string root, string path) =>
        Path.GetRelativePath(root, path).Count(c => c == Path.DirectorySeparatorChar);

    private static string? Node(string root)
    {
        var path = Path.Combine(root, "package.json");
        if (!File.Exists(path))
            return null;

        var runner = File.Exists(Path.Combine(root, "pnpm-lock.yaml")) ? "pnpm"
            : File.Exists(Path.Combine(root, "yarn.lock")) ? "yarn"
            : File.Exists(Path.Combine(root, "bun.lockb")) || File.Exists(Path.Combine(root, "bun.lock")) ? "bun"
            : "npm";

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
            {
                foreach (var script in new[] { "dev", "start" })
                {
                    if (scripts.TryGetProperty(script, out _))
                        return $"{runner} run {script}";
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        return null;
    }

    private static string? ActiveScript(string? file)
    {
        if (file is null)
            return null;
        return Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".py" => $"python \"{file}\"",
            ".js" or ".mjs" => $"node \"{file}\"",
            ".ps1" => $"powershell -NoProfile -File \"{file}\"",
            ".bat" or ".cmd" => $"\"{file}\"",
            _ => null,
        };
    }

    // O interpretador de comandos mais moderno disponível nesta máquina.
    public static string FindShell()
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
        {
            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder.Trim(), name);
                if (File.Exists(candidate))
                    return $"\"{candidate}\" -NoLogo";
            }
        }
        return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
    }

    public static string WrapForShell(string command)
    {
        var shell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{shell}\" /d /s /c \"{command}\"";
    }
}
