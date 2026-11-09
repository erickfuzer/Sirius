using System.Text.Json;

namespace Sirius.Core;

public sealed class SessionFile
{
    public string Path { get; set; } = "";
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
}

public sealed class SessionState
{
    public List<SessionFile> Files { get; set; } = new();
    public string? ActivePath { get; set; }

    // Os pontos de parada de cada arquivo, mesmo o que não está aberto em aba.
    public Dictionary<string, List<int>> Breakpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // As condições desses pontos, como $i == 5, por arquivo e linha. Só as linhas que têm condição.
    public Dictionary<string, Dictionary<int, string>> BreakpointConditions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // As expressões vigiadas na depuração, como $total * 2.
    public List<string> Watches { get; set; } = new();
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Theme { get; set; } = "claro";
    public string? LastFolder { get; set; }
    public double SidebarWidth { get; set; } = 232;
    public double EditorFontSize { get; set; } = 13.5;
    public bool WordWrap { get; set; }

    // Recuo do editor: quantos espaços, ou um Tab de verdade. Vale também para o Emmet e para formatar.
    public int IndentSize { get; set; } = 4;
    public bool IndentWithTabs { get; set; }

    // Aspas, parênteses, chaves e tags de HTML que se fecham ao digitar.
    public bool AutoClosePairs { get; set; } = true;

    // O desenho ao lado do código, nos arquivos .svg.
    public bool SvgPreview { get; set; } = true;

    // Salvar um segundo depois de parar de digitar, como o Auto Save do VS Code. Desligado de fábrica.
    public bool AutoSave { get; set; }

    // A pessoa ligou "Depurar páginas do servidor" e não desligou: a Sirius volta a ouvir ao abrir.
    public bool ListenToServer { get; set; }

    // As últimas pastas abertas, a mais recente primeiro.
    public List<string> RecentFolders { get; set; } = new();

    public const int RecentFolderLimit = 8;

    public void RememberFolder(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > RecentFolderLimit)
            RecentFolders.RemoveRange(RecentFolderLimit, RecentFolders.Count - RecentFolderLimit);
    }

    // Aviso de versão nova. A última versão fica guardada para o aviso aparecer mesmo sem internet.
    public bool CheckForUpdates { get; set; } = true;
    public string? LatestVersion { get; set; }
    public string? DismissedVersion { get; set; }

    // Comando de execução escolhido para cada pasta, guardado aqui e não no projeto:
    // abrir um repositório de terceiros nunca define o que o botão Executar faz.
    public Dictionary<string, string> RunCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // As abas de cada pasta, para reabrir na próxima vez. A chave é o caminho em minúsculas.
    public Dictionary<string, SessionState> Sessions { get; set; } = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sirius", "settings.json");

    public static AppSettings Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Perder a preferência não deve impedir o uso da IDE.
        }
    }
}
