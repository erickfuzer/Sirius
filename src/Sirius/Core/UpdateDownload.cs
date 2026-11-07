using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// O instalador de uma versão, como a Release do GitHub o descreve. Sha256 é o que o GitHub registrou ao receber o arquivo.
public sealed record UpdatePackage(string Name, Uri Url, long Size, string Sha256);

// Atualizar pela própria Sirius: acha o instalador da versão, baixa, confere e o roda em silêncio.
// Não há assinatura digital ainda, então a confiança é a mesma de baixar da página de Releases:
// só esse endereço é aceito, e o arquivo precisa ser exatamente o que o GitHub registrou.
public static partial class UpdateDownload
{
    private const string ReleaseApi = "https://api.github.com/repos/erickfuzer/Sirius/releases/tags/";
    private const string DownloadPrefix = "https://github.com/erickfuzer/Sirius/releases/download/";
    public const long MaxBytes = 300L * 1024 * 1024;
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    // O instalador sai em silêncio, mostrando só o andamento, e abre a Sirius de novo no fim (relaunch, em sirius.iss).
    public const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LANG=ptbr /relaunch=1";

    private static readonly HttpClient Http = CreateClient();

    [GeneratedRegex("^sha256:([0-9a-f]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sirius", "atualizacao");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sirius", AppVersion.Current.Text));
        return client;
    }

    // O instalador da Release. O site também procura o arquivo que termina em Setup.exe.
    public static UpdatePackage? ParsePackage(string json, AppVersion version)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object
                    || asset.TryGetProperty("name", out var nameValue) is false || nameValue.GetString() is not { } name
                    || !name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    continue;

                // Só o endereço de download desta versão, neste repositório.
                var url = asset.TryGetProperty("browser_download_url", out var urlValue) ? urlValue.GetString() : null;
                if (url is null || !url.StartsWith(DownloadPrefix + version.Tag + "/", StringComparison.Ordinal)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    continue;

                // Sem o registro do GitHub não há como conferir o arquivo, e então ele não é usado.
                var digest = asset.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind == JsonValueKind.String
                    ? Digest().Match(digestValue.GetString()!)
                    : null;
                var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                if (digest is not { Success: true } || size <= 0 || size > MaxBytes)
                    continue;

                return new UpdatePackage(name, uri, size, digest.Groups[1].Value);
            }
            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static async Task<UpdatePackage?> FindAsync(AppVersion version, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi + version.Tag);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(LookupTimeout);
        using var response = await Http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            return null;
        return ParsePackage(await response.Content.ReadAsStringAsync(timeout.Token), version);
    }

    // Baixa para a pasta e devolve o caminho só se o arquivo for exatamente o registrado. Um que já
    // estava lá, inteiro, é aproveitado: quem adiou a atualização não baixa de novo.
    public static async Task<string?> DownloadAsync(UpdatePackage package, string folder, Func<Uri, CancellationToken, Task<Stream>> open,
        IProgress<double>? progress, CancellationToken cancel)
    {
        var target = Path.Combine(folder, package.Name);
        var partial = target + ".parcial";
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(target) && await HashOfFileAsync(target, cancel) == package.Sha256)
            {
                progress?.Report(1);
                return target;
            }

            // Instaladores de outras versões, que não servem mais.
            foreach (var old in Directory.EnumerateFiles(folder))
                TryDelete(old);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var source = await open(package.Url, cancel))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                // Mais do que o registrado já não é o arquivo certo: para de ler e descarta.
                while (total <= package.Size && (read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    total += read;
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                    progress?.Report(Math.Min(1, (double)total / package.Size));
                }
            }

            if (total != package.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != package.Sha256)
                return Fail(partial);
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        {
            return Fail(partial);
        }
    }

    // Acha, baixa e confere a versão, ou devolve null em qualquer falha.
    public static async Task<string?> DownloadVersionAsync(AppVersion version, IProgress<double>? progress, CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(DownloadTimeout);
            if (await FindAsync(version, timeout.Token) is not { } package)
                return null;
            return await DownloadAsync(package, Folder, OpenAsync, progress, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static async Task<Stream> OpenAsync(Uri url, CancellationToken cancel)
    {
        var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancel);
    }

    public static bool StartInstaller(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, InstallerArguments) { UseShellExecute = false });
            return process is not null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static async Task<string> HashOfFileAsync(string path, CancellationToken cancel)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel));
    }

    private static string? Fail(string partial)
    {
        TryDelete(partial);
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fica para a próxima vez: um arquivo velho na pasta não atrapalha.
        }
    }
}
