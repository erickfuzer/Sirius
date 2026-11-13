using System.Diagnostics;

namespace Sirius.Core;

// Onde o Apache ligado mostra os arquivos: a porta do Listen e a pasta do DocumentRoot, lidos do httpd.conf
// ao lado do httpd.exe que está rodando. Um projeto dentro dessa pasta abre pelo Apache, com o PHP dele.
public sealed record ApacheSite(string Origin, string DocumentRoot)
{
    public static ApacheSite? FindRunning()
    {
        foreach (var process in Process.GetProcessesByName("httpd"))
        {
            try
            {
                if (process.MainModule?.FileName is not { } exe)
                    continue;
                var serverRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, ".."));
                var conf = Path.Combine(serverRoot, "conf", "httpd.conf");
                if (File.Exists(conf) && FromConfig(File.ReadAllText(conf), serverRoot) is { } site)
                    return site;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Um processo que já fechou ou de outra conta.
            }
            finally
            {
                process.Dispose();
            }
        }
        return null;
    }

    // Só o que vale para o servidor todo: o DocumentRoot de um VirtualHost é de outro endereço.
    public static ApacheSite? FromConfig(string text, string? serverRoot)
    {
        var defines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? listen = null, documentRoot = null;
        var insideHost = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("<VirtualHost", StringComparison.OrdinalIgnoreCase))
            {
                insideHost++;
                continue;
            }
            if (line.StartsWith("</VirtualHost", StringComparison.OrdinalIgnoreCase))
            {
                insideHost = Math.Max(0, insideHost - 1);
                continue;
            }
            if (insideHost > 0)
                continue;

            var (name, value) = Split(line);
            value = Expand(value, defines);
            switch (name.ToLowerInvariant())
            {
                case "define":
                    var (key, defined) = Split(value);
                    defines[key] = Unquote(defined);
                    break;
                case "serverroot":
                    serverRoot ??= Unquote(value);
                    break;
                case "listen":
                    listen ??= value;
                    break;
                case "documentroot":
                    documentRoot ??= Unquote(value);
                    break;
            }
        }

        if (documentRoot is null)
            return null;
        var port = 80;
        if (listen is not null)
        {
            // "80", "127.0.0.1:8080" ou "[::]:80": a porta é o número depois do último dois-pontos.
            var portText = listen.Split(' ')[0];
            portText = portText[(portText.LastIndexOf(':') + 1)..];
            if (!int.TryParse(portText, out port) || port is <= 0 or > 65535)
                port = 80;
        }

        documentRoot = documentRoot.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(documentRoot))
        {
            if (serverRoot is null)
                return null;
            documentRoot = Path.Combine(serverRoot, documentRoot);
        }
        try
        {
            documentRoot = Path.GetFullPath(documentRoot).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return new ApacheSite(port == 80 ? "http://localhost" : $"http://localhost:{port}", documentRoot);
    }

    // O caminho no endereço de um arquivo ou pasta dentro do DocumentRoot, como /trabalho/index.php.
    public string? PathFor(string path)
    {
        var full = Path.GetFullPath(path);
        if (!LiveServer.IsInside(DocumentRoot, full))
            return null;
        var relative = Path.GetRelativePath(DocumentRoot, full);
        if (relative == ".")
            return "/";
        var url = "/" + string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
        return Directory.Exists(full) ? url + "/" : url;
    }

    private static (string Name, string Value) Split(string line)
    {
        var space = line.IndexOfAny([' ', '\t']);
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..].Trim());
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static string Expand(string value, Dictionary<string, string> defines)
    {
        foreach (var (key, defined) in defines)
            value = value.Replace("${" + key + "}", defined, StringComparison.OrdinalIgnoreCase);
        return value;
    }
}
