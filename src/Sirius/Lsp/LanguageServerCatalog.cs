using System.Diagnostics;
using Sirius.Core;

namespace Sirius.Lsp;

// Um servidor de linguagem conhecido e como obtê-lo.
public sealed record ServerSpec(string Id, string DisplayName, string Executable, string[] Arguments, ServerInstaller Installer);

public enum ServerInstaller { DotNetTool, Npm, Manual }

// A conexão com um servidor em execução, e como encerrá-lo.
public sealed record ServerConnection(Stream Input, Stream Output, IDisposable Owner);

public static class LanguageServerCatalog
{
    private static readonly ServerSpec CSharp = new("csharp", "C#", "csharp-ls", [], ServerInstaller.DotNetTool);
    private static readonly ServerSpec Php = new("php", "PHP", "intelephense", ["--stdio"], ServerInstaller.Npm);
    private static readonly ServerSpec TypeScript = new("typescript", "JavaScript e TypeScript", "typescript-language-server", ["--stdio"], ServerInstaller.Npm);
    private static readonly ServerSpec Python = new("python", "Python", "pyright-langserver", ["--stdio"], ServerInstaller.Npm);
    private static readonly ServerSpec Go = new("go", "Go", "gopls", [], ServerInstaller.Manual);
    private static readonly ServerSpec Rust = new("rust", "Rust", "rust-analyzer", [], ServerInstaller.Manual);
    private static readonly ServerSpec Cpp = new("cpp", "C e C++", "clangd", [], ServerInstaller.Manual);

    private static readonly Dictionary<string, (ServerSpec Server, string LanguageId)> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = (CSharp, "csharp"),
        [".php"] = (Php, "php"),
        [".ts"] = (TypeScript, "typescript"),
        [".tsx"] = (TypeScript, "typescriptreact"),
        [".js"] = (TypeScript, "javascript"),
        [".jsx"] = (TypeScript, "javascriptreact"),
        [".mjs"] = (TypeScript, "javascript"),
        [".py"] = (Python, "python"),
        [".go"] = (Go, "go"),
        [".rs"] = (Rust, "rust"),
        [".c"] = (Cpp, "c"),
        [".h"] = (Cpp, "c"),
        [".cpp"] = (Cpp, "cpp"),
        [".hpp"] = (Cpp, "cpp"),
    };

    public static (ServerSpec Server, string LanguageId)? ForPath(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var entry) ? entry : null;

    public static string? FindExecutable(string name)
    {
        ToolPaths.EnsureOnPath();
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var folder in folders)
        {
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                var candidate = Path.Combine(folder.Trim(), name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    // O comando que instala o servidor, ou null quando a instalação é manual.
    public static string? InstallCommand(ServerSpec server)
    {
        switch (server.Installer)
        {
            case ServerInstaller.DotNetTool:
                return $"dotnet tool install -g {server.Executable}";
            case ServerInstaller.Npm:
                var package = server.Id switch
                {
                    "typescript" => "typescript typescript-language-server",
                    "python" => "pyright",
                    _ => server.Executable,
                };
                var nodeFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");
                var install = $"set \"PATH={nodeFolder};%PATH%\" && npm install -g {package}";
                return FindExecutable("npm") is not null
                    ? install
                    : $"winget install --id OpenJS.NodeJS.LTS --exact --source winget --silent --accept-package-agreements --accept-source-agreements && {install}";
            default:
                return null;
        }
    }

    public static ServerConnection? Launch(ServerSpec server, string root)
    {
        var executable = FindExecutable(server.Executable);
        if (executable is null)
            return null;

        var info = new ProcessStartInfo
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Os atalhos do npm são arquivos .cmd, que precisam do cmd para rodar.
        if (executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = executable;
            foreach (var argument in server.Arguments)
                info.ArgumentList.Add(argument);
        }
        else
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = $"/d /s /c \"\"{executable}\" {string.Join(' ', server.Arguments)}\"";
        }

        var process = Process.Start(info);
        if (process is null)
            return null;

        // Mensagens de log do servidor são descartadas, senão o canal enche e ele trava.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return new ServerConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, new ServerProcess(process));
    }

    private sealed class ServerProcess(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // O servidor já tinha terminado.
            }
            process.Dispose();
        }
    }
}
