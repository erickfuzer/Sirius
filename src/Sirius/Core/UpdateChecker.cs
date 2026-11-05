using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// Versão no formato 1.2.3-alpha.4, com a ordem do versionamento semântico:
// alpha.9 vem antes de alpha.10, e 1.0.0-alpha vem antes de 1.0.0.
public sealed partial class AppVersion : IComparable<AppVersion>
{
    private readonly long[] _core;
    private readonly string[] _prerelease;

    private AppVersion(string tag, string text, long[] core, string[] prerelease)
    {
        Tag = tag;
        Text = text;
        _core = core;
        _prerelease = prerelease;
    }

    // Como veio, com o "v", para montar o endereço da Release.
    public string Tag { get; }

    // Para mostrar na tela, sem o "v".
    public string Text { get; }

    public static AppVersion Current { get; } = ReadCurrent();

    private static AppVersion ReadCurrent()
    {
        var text = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Parse(text) ?? Parse("0.0.0")!;
    }

    // A forma rígida também protege o endereço: uma etiqueta aceita aqui nunca tem barra nem espaço.
    [GeneratedRegex(@"^v?(\d{1,9})\.(\d{1,9})\.(\d{1,9})(?:-([0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Format();

    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = Format().Match(text);
        if (!match.Success)
            return null;

        long[] core = [long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value), long.Parse(match.Groups[3].Value)];
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        // O que vem depois do "+" é o código do commit, que não muda a versão.
        var tag = text.Split('+')[0];
        return new AppVersion(tag, tag.TrimStart('v'), core, prerelease);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
            return 1;
        for (var i = 0; i < _core.Length; i++)
        {
            var order = _core[i].CompareTo(other._core[i]);
            if (order != 0)
                return order;
        }

        // A versão final vem depois de qualquer pré-versão dela.
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
            return other._prerelease.Length.CompareTo(_prerelease.Length);

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var order = ComparePart(_prerelease[i], other._prerelease[i]);
            if (order != 0)
                return order;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static int ComparePart(string left, string right)
    {
        var leftIsNumber = long.TryParse(left, out var leftNumber);
        var rightIsNumber = long.TryParse(right, out var rightNumber);
        if (leftIsNumber && rightIsNumber)
            return leftNumber.CompareTo(rightNumber);
        if (leftIsNumber != rightIsNumber)
            return leftIsNumber ? -1 : 1;
        return string.CompareOrdinal(left, right);
    }

    public override string ToString() => Text;
}

public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/erickfuzer/Sirius/releases/latest";
    private const string ReleasesPage = "https://github.com/erickfuzer/Sirius/releases/tag/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // O GitHub limita consultas sem login, e as notas de uma versão cabem com folga nesse tamanho.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sirius", AppVersion.Current.Text));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // Devolve null se não der para saber: sem internet ou limite atingido não atrapalham quem está programando.
    public static async Task<string?> FetchLatestTagAsync(CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(RequestTimeout);
            return ParseLatestTag(await Http.GetStringAsync(LatestReleaseApi, timeout.Token));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string? ParseLatestTag(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("tag_name", out var tag)
                   && tag.ValueKind == JsonValueKind.String
                   && AppVersion.Parse(tag.GetString()) is not null
                ? tag.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A versão a anunciar, ou null se a pessoa já está nela ou decidiu ignorá-la.
    public static AppVersion? Pending(AppVersion current, string? latestTag, string? dismissedTag)
    {
        if (AppVersion.Parse(latestTag) is not { } latest || latest.CompareTo(current) <= 0)
            return null;
        return AppVersion.Parse(dismissedTag) is { } dismissed && latest.CompareTo(dismissed) <= 0 ? null : latest;
    }

    public static string ReleasePageFor(AppVersion version) => ReleasesPage + version.Tag;

    public static bool OpenInBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
