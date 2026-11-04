using System.Text.Json;

namespace Sirius.Core;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Theme { get; set; } = "claro";
    public string? LastFolder { get; set; }
    public double SidebarWidth { get; set; } = 232;
    public double EditorFontSize { get; set; } = 13.5;

    // Comando de execução escolhido para cada pasta, guardado aqui e não no projeto:
    // abrir um repositório de terceiros nunca define o que o botão Executar faz.
    public Dictionary<string, string> RunCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
