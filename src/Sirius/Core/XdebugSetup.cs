using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// Qual PHP é este: o Xdebug precisa ser feito exatamente para a mesma versão e o mesmo compilador.
public sealed record PhpBuild(string Version, bool ThreadSafe, int VisualStudio, bool Is64Bit);

public sealed record PhpProbe(PhpBuild Build, bool XdebugLoaded);

public sealed record XdebugRelease(string FileName, string Sha256)
{
    public string Url => "https://xdebug.org/files/" + FileName;
}

// A lista oficial de xdebug.org. Os códigos ficam aqui, e não vêm do site na hora de baixar,
// porque o PHP vai carregar esse arquivo: só roda o que confere com o que foi conferido antes.
public static class XdebugCatalog
{
    public const string Version = "3.5.3";

    private static readonly Dictionary<string, string> Hashes = new()
    {
        ["8.0-nts-vs16"] = "a3ffd4d5b1f9ca0766b1f3ed321246808a51ce4ba49dc32ae195bfafc4d4d9a2",
        ["8.0-ts-vs16"] = "48398f54d7bf311728eafeb9999a5a55be5b5feb83b9af8f3115fef4b71dd2f7",
        ["8.1-nts-vs16"] = "15bb3abcd0755af8378550793bcf7603636add31dc4a04d24bfd2a1f764399b4",
        ["8.1-ts-vs16"] = "8a90f1fd3e199a07e3039fc857330ca098a71bf9622e88a878efaa1b1b6fe358",
        ["8.2-nts-vs16"] = "b449eda7eeefc80c651fac334582c5f0877cd72cfe55fc1c5f0f98bf6a4edb94",
        ["8.2-ts-vs16"] = "56a62ebd342a143ced245f30de375f5b907ba50efaacf42801a5dd280006e127",
        ["8.3-nts-vs16"] = "4c80487592484c36d21a7006a53738dfc07884fc6867720ddc5c11559371cdce",
        ["8.3-ts-vs16"] = "c178f05068a6f0cef4a37eaca6003a56120004bf4ac20db6ff20f6f1ce0b439e",
        ["8.4-nts-vs17"] = "a94d4fcfe4086a300b2decaf7c04e59f4981aa5e56992d48b55c5e49dfd9b9ee",
        ["8.4-ts-vs17"] = "b9c79caec87f2b289df4b7bfa64b64f01c97956c9ac7ddb936302b79aec65cda",
        ["8.5-nts-vs17"] = "0a0d52bf276dad7e5f90ac30a5110b3119d291b20c118be0cb7f5752064f1811",
        ["8.5-ts-vs17"] = "c75623ba050ddef734ba28a957e92df9ce5b8dfdb1a0569e5071bd6b68a12292",
    };

    public static XdebugRelease? Find(PhpBuild build)
    {
        if (!build.Is64Bit)
            return null;
        var key = $"{build.Version}-{(build.ThreadSafe ? "ts" : "nts")}-vs{build.VisualStudio}";
        return Hashes.TryGetValue(key, out var hash)
            ? new XdebugRelease($"php_xdebug-{Version}-{key}-x86_64.dll", hash)
            : null;
    }
}

public static partial class XdebugSetup
{
    private const int MaxDownloadBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = new() { Timeout = DownloadTimeout };

    [GeneratedRegex(@"^PHP Version => (\d+\.\d+)", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"^PHP Extension Build => API\d+,(TS|NTS),VS(\d+)", RegexOptions.Multiline)]
    private static partial Regex BuildLine();

    [GeneratedRegex(@"^Architecture => (\S+)", RegexOptions.Multiline)]
    private static partial Regex ArchitectureLine();

    // Fora da pasta da instalação do PHP, que pode ser do XAMPP e não é nossa para mexer.
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sirius", "xdebug");

    // Lê a saída de "php -i". Null quando não é um PHP para Windows que dê para reconhecer.
    public static PhpBuild? ParseInfo(string info)
    {
        var version = VersionLine().Match(info);
        var build = BuildLine().Match(info);
        if (!version.Success || !build.Success)
            return null;
        return new PhpBuild(
            version.Groups[1].Value,
            build.Groups[1].Value == "TS",
            int.Parse(build.Groups[2].Value),
            ArchitectureLine().Match(info) is { Success: true } architecture && architecture.Groups[1].Value == "x64");
    }

    public static async Task<PhpProbe?> ProbeAsync(string php)
    {
        // Sem o php.ini, a informação vem igual e nada de fora é carregado só para perguntar.
        var info = await RunAsync(php, "-n -i");
        if (info is null || ParseInfo(info) is not { } build)
            return null;
        var version = await RunAsync(php, "-v");
        return new PhpProbe(build, version?.Contains("with Xdebug", StringComparison.OrdinalIgnoreCase) == true);
    }

    // O php.ini que este PHP lê, segundo ele mesmo. Null sem php.ini ou se o PHP não respondeu.
    public static async Task<string?> LoadedIniAsync(string php) =>
        await RunAsync(php, "--ini") is { } output ? PhpIniXdebug.ParseLoadedIni(output) : null;

    private static async Task<string?> RunAsync(string executable, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Fora do projeto aberto, que não é confiável por padrão.
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            if (process is null)
                return null;

            using var cancel = new CancellationTokenSource(ProbeTimeout);
            var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
            var error = process.StandardError.ReadToEndAsync(cancel.Token);
            await Task.WhenAll(output, error);
            await process.WaitForExitAsync(cancel.Token);
            return process.ExitCode == 0 ? await output : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    // Só vale um arquivo idêntico ao da lista: um download pela metade nunca é carregado.
    public static string? InstalledFile(XdebugRelease release, string folder)
    {
        var path = Path.Combine(folder, release.FileName);
        try
        {
            return File.Exists(path) && Matches(File.ReadAllBytes(path), release.Sha256) ? path : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool Matches(byte[] content, string sha256) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(content)), sha256, StringComparison.OrdinalIgnoreCase);

    public static Task<byte[]> FetchAsync(string url, CancellationToken cancel) => Http.GetByteArrayAsync(url, cancel);

    // Devolve o caminho do arquivo pronto. Null se a internet falhar ou se ele não conferir.
    public static async Task<string?> DownloadAsync(XdebugRelease release, string folder, Func<string, CancellationToken, Task<byte[]>> fetch)
    {
        try
        {
            using var cancel = new CancellationTokenSource(DownloadTimeout);
            var content = await fetch(release.Url, cancel.Token);
            if (content.Length > MaxDownloadBytes || !Matches(content, release.Sha256))
                return null;

            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, release.FileName);
            var temporary = path + ".baixando";
            await File.WriteAllBytesAsync(temporary, content, cancel.Token);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Vai em um arquivo à parte, lido pelo PHP só durante esta execução. O php.ini fica como está.
    public static string IniText(string? extensionFile)
    {
        var text = new StringBuilder();
        if (extensionFile is not null)
            text.Append("zend_extension=\"").Append(extensionFile.Replace('\\', '/')).Append("\"\n");
        text.Append("xdebug.mode=debug\n");
        text.Append("xdebug.start_with_request=yes\n");
        text.Append("xdebug.client_host=127.0.0.1\n");
        // A espera por quem não está ouvindo nunca pode atrasar a página.
        text.Append("xdebug.connect_timeout_ms=200\n");
        text.Append("xdebug.log_level=0\n");
        return text.ToString();
    }

    public static string WriteIni(string folder, string name, string? extensionFile)
    {
        var iniFolder = Path.Combine(folder, "ini", name);
        Directory.CreateDirectory(iniFolder);
        File.WriteAllText(Path.Combine(iniFolder, "sirius-xdebug.ini"), IniText(extensionFile), new UTF8Encoding(false));
        return iniFolder;
    }

    // O comando continua o da pessoa. As variáveis valem só para ele e para os PHP que ele chamar,
    // como o do "artisan serve", e por isso aqui, e não como opção do "php".
    public static string Command(string command, string iniFolder, int port) =>
        $"set \"PHP_INI_SCAN_DIR={iniFolder}\"&& set \"XDEBUG_MODE=debug\"&& set \"XDEBUG_CONFIG=client_port={port}\"&& {command}";
}
