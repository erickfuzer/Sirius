using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Sirius.Theming;

// Um ícone pequeno na frente de cada arquivo, como no VS Code com o Material Icon Theme, que é o que o dono
// usa: o index.php com o PHP em azul. Os desenhos vêm de Assets\FileIcons\icons.json (licença MIT, ao lado),
// gerado por gerar.py. Arquivo desconhecido ganha uma folha simples na cor da paleta. As pastas também
// vêm do Material, fechadas e abertas, pelo nome: css, js, img, includes, public.
public static class FileIcons
{
    public const double Size = 16;

    // Cada camada é uma cor e um caminho. Alguns desenhos de pasta vêm deslocados ou encolhidos (Transform).
    private sealed record Layer(Color Color, string Data, Matrix? Transform = null);

    private sealed record IconData(Rect Box, Layer[] Layers);

    // A pasta é sempre o mesmo desenho, fechado ou aberto, numa cor, com o desenho de dentro por cima.
    private sealed record FolderData(Color Color, Layer[] Motive);

    private sealed record Table(
        Dictionary<string, IconData> Files, Dictionary<string, FolderData> Folders, Dictionary<string, string> FolderNames,
        FolderData Folder, string Closed, string Open);

    private static readonly Lazy<Table> Data = new(Load);
    private static readonly Dictionary<(string Key, string Theme), IImage> Cache = [];

    // Os nomes exatos vêm antes da extensão, como no VS Code: o README.md é o "i" azul, não o Markdown.
    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["readme.md"] = "readme", ["readme.txt"] = "readme", ["readme"] = "readme",
        ["license"] = "license", ["license.md"] = "license", ["license.txt"] = "license", ["licence"] = "license",
        ["copying"] = "license", ["copying.txt"] = "license",
        ["package.json"] = "nodejs", ["package-lock.json"] = "nodejs", [".nvmrc"] = "nodejs",
        [".gitignore"] = "git", [".gitattributes"] = "git", [".gitmodules"] = "git", [".gitkeep"] = "git", [".gitconfig"] = "git",
        [".htaccess"] = "xml", [".editorconfig"] = "settings",
        ["dockerfile"] = "docker", ["docker-compose.yml"] = "docker", ["docker-compose.yaml"] = "docker", ["compose.yml"] = "docker", ["compose.yaml"] = "docker",
        ["artisan"] = "laravel",
        ["phpunit.xml"] = "phpunit", ["phpunit.xml.dist"] = "phpunit",
        ["todo.md"] = "todo",
    };

    private static readonly Dictionary<string, string> ByExtension = Extensions(new()
    {
        ["html"] = ["html", "htm", "xhtml"],
        ["php"] = ["php", "phtml", "php4", "php5", "ctp"],
        ["laravel"] = ["blade.php"],
        ["css"] = ["css"],
        ["sass"] = ["scss", "sass"],
        ["less"] = ["less"],
        ["javascript"] = ["js", "mjs", "cjs"],
        ["typescript"] = ["ts", "mts", "cts"],
        ["react"] = ["jsx"],
        ["react_ts"] = ["tsx"],
        ["vue"] = ["vue"],
        ["svelte"] = ["svelte"],
        ["json"] = ["json", "jsonc", "json5", "webmanifest", "map"],
        ["markdown"] = ["md", "markdown"],
        ["document"] = ["txt"],
        ["image"] = ["png", "jpg", "jpeg", "jfif", "gif", "webp", "bmp", "ico", "avif", "tif", "tiff"],
        ["svg"] = ["svg"],
        ["xml"] = ["xml", "xsd", "xsl", "xslt", "plist", "axaml", "resx"],
        ["xaml"] = ["xaml"],
        ["yaml"] = ["yml", "yaml"],
        ["toml"] = ["toml"],
        ["settings"] = ["ini", "conf", "config", "cfg", "properties"],
        ["tune"] = ["env"],
        ["git"] = ["patch"],
        ["console"] = ["sh", "bash", "zsh", "bat", "cmd"],
        ["powershell"] = ["ps1", "psm1", "psd1"],
        ["database"] = ["sql", "sqlite", "sqlite3", "db"],
        ["csharp"] = ["cs", "csx"],
        ["visualstudio"] = ["csproj", "sln", "slnx", "vbproj", "vcxproj"],
        ["c"] = ["c"],
        ["h"] = ["h"],
        ["cpp"] = ["cpp", "cc", "cxx", "c++"],
        ["hpp"] = ["hpp", "hh", "hxx"],
        ["python"] = ["py", "pyw"],
        ["java"] = ["java", "jsp"],
        ["go"] = ["go"],
        ["rust"] = ["rs"],
        ["ruby"] = ["rb", "erb"],
        ["log"] = ["log"],
        ["lock"] = ["lock"],
        ["zip"] = ["zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz"],
        ["pdf"] = ["pdf"],
        ["table"] = ["csv", "tsv", "xlsx", "xls", "ods"],
        ["word"] = ["doc", "docx", "rtf", "odt"],
        ["font"] = ["ttf", "otf", "woff", "woff2", "eot"],
        ["audio"] = ["mp3", "wav", "flac", "m4a", "aac", "opus", "mid"],
        ["video"] = ["mp4", "webm", "mkv", "mov", "avi", "ogg", "ogv", "wmv"],
        ["exe"] = ["exe", "msi"],
        ["certificate"] = ["crt", "cer", "cert"],
        ["key"] = ["pem", "key", "pub"],
        ["twig"] = ["twig"],
        ["todo"] = ["todo"],
    });

    private static Dictionary<string, string> Extensions(Dictionary<string, string[]> table)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (icon, extensions) in table)
            foreach (var extension in extensions)
                result.Add(extension, icon);
        return result;
    }

    // O nome do ícone, ou null para um arquivo que nenhum ícone descreve. A extensão mais longa vence,
    // como no VS Code: index.blade.php é Laravel, e .env, sem nome antes do ponto, ainda é "env".
    public static string? KeyFor(string fileName)
    {
        if (ByName.TryGetValue(fileName, out var named))
            return named;
        // .env.local, .env.example e afins são o mesmo .env.
        if (fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            return "tune";
        for (var dot = fileName.IndexOf('.'); dot >= 0 && dot < fileName.Length - 1; dot = fileName.IndexOf('.', dot + 1))
        {
            if (ByExtension.TryGetValue(fileName[(dot + 1)..], out var icon))
                return icon;
        }
        return null;
    }

    // O desenho para o tema atual, guardado: as linhas da árvore comparam o ícone ao trocar a lista.
    public static IImage For(string fileName) => ForKey(KeyFor(fileName), Palette.Current);

    public static IImage ForKey(string? key, Palette palette)
    {
        key ??= "";
        // O TOML claro sumiria no tema claro: o Material tem uma versão escura dele para isso.
        if (key == "toml" && palette.Variant == Avalonia.Styling.ThemeVariant.Light)
            key = "toml_light";
        if (Cache.TryGetValue((key, palette.Name), out var cached))
            return cached;
        var image = key.Length > 0 && Data.Value.Files.TryGetValue(key, out var drawing) ? Build(drawing) : Plain(palette);
        Cache[(key, palette.Name)] = image;
        return image;
    }

    // Pastas

    // O ícone da pasta pelo nome, como no Material: css, js, img, includes e mais umas novecentas. Também
    // valem com ".", "_" ou "-" na frente e entre "__", como .vscode e __tests__. Null é a pasta comum.
    public static string? FolderKeyFor(string folderName)
    {
        var names = Data.Value.FolderNames;
        var name = folderName.ToLowerInvariant();
        if (names.TryGetValue(name, out var icon))
            return icon;
        if (name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal))
            return names.GetValueOrDefault(name[2..^2]);
        if (name.Length > 1 && name[0] is '.' or '_' or '-')
            return names.GetValueOrDefault(name[1..]);
        return null;
    }

    public static IImage ForFolder(string folderName, bool open) => ForFolderKey(FolderKeyFor(folderName), open, Palette.Current);

    public static IImage ForFolderKey(string? key, bool open, Palette palette)
    {
        var cacheKey = (key ?? "folder") + (open ? "-open" : "");
        if (Cache.TryGetValue((cacheKey, palette.Name), out var cached))
            return cached;
        var table = Data.Value;
        var folder = key is not null && table.Folders.TryGetValue(key, out var found) ? found : table.Folder;
        var layers = new[] { new Layer(folder.Color, open ? table.Open : table.Closed) }.Concat(folder.Motive).ToArray();
        var image = Build(new IconData(new Rect(0, 0, 16, 16), layers));
        Cache[(cacheKey, palette.Name)] = image;
        return image;
    }

    private static Table Load()
    {
        var files = new Dictionary<string, IconData>();
        var folders = new Dictionary<string, FolderData>();
        var names = new Dictionary<string, string>();
        using var stream = typeof(FileIcons).Assembly.GetManifestResourceStream("FileIcons.icons.json");
        if (stream is null)
            return new Table(files, folders, names, new FolderData(Colors.Gray, []), "", "");

        static Layer[] Layers(JsonElement list) => list.EnumerateArray().Select(layer =>
        {
            Matrix? transform = null;
            if (layer.GetArrayLength() > 2)
            {
                var m = layer[2].EnumerateArray().Select(v => v.GetDouble()).ToArray();
                transform = new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]);
            }
            return new Layer(Color.Parse(layer[0].GetString()!), layer[1].GetString()!, transform);
        }).ToArray();

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        foreach (var icon in root.GetProperty("icons").EnumerateObject())
        {
            var box = icon.Value.GetProperty("box").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            files[icon.Name] = new IconData(new Rect(box[0], box[1], box[2], box[3]), Layers(icon.Value.GetProperty("layers")));
        }
        foreach (var folder in root.GetProperty("folders").EnumerateObject())
            folders[folder.Name] = new FolderData(Color.Parse(folder.Value.GetProperty("color").GetString()!), Layers(folder.Value.GetProperty("motive")));
        foreach (var name in root.GetProperty("folderNames").EnumerateObject())
            names[name.Name] = name.Value.GetString()!;
        var plain = root.GetProperty("folder");
        return new Table(files, folders, names, new FolderData(Color.Parse(plain.GetProperty("color").GetString()!), []),
            plain.GetProperty("closed").GetString()!, plain.GetProperty("open").GetString()!);
    }

    // O desenho no quadrado de 16, do tamanho do viewBox: um retângulo invisível segura a caixa, senão
    // o Avalonia esticaria o desenho até as bordas do que foi pintado.
    private static DrawingImage Build(IconData drawing)
    {
        var scale = Size / Math.Max(drawing.Box.Width, drawing.Box.Height);
        var inner = new DrawingGroup
        {
            Transform = new MatrixTransform(Matrix.CreateTranslation(-drawing.Box.X, -drawing.Box.Y) * Matrix.CreateScale(scale, scale)),
        };
        foreach (var layer in drawing.Layers)
        {
            var geometry = Geometry.Parse(layer.Data);
            if (layer.Transform is { } transform)
                geometry.Transform = new MatrixTransform(transform);
            inner.Children.Add(new GeometryDrawing { Brush = new ImmutableSolidColorBrush(layer.Color), Geometry = geometry });
        }
        return Framed(inner);
    }

    // A folha com a ponta dobrada, como a dos arquivos sem ícone no VS Code.
    private static DrawingImage Plain(Palette palette)
    {
        var page = new GeometryDrawing
        {
            Pen = new ImmutablePen(palette.Muted.ToImmutable(), 1.1, lineJoin: PenLineJoin.Round),
            Geometry = Geometry.Parse("M4.5 2.5 H9.5 L12.5 5.5 V13.5 H4.5 Z M9.5 2.5 V5.5 H12.5"),
        };
        var group = new DrawingGroup();
        group.Children.Add(page);
        return Framed(group);
    }

    // O Viewbox corta o que passa do quadrado, como o SVG faz: o desenho da pasta do Sass sai um pouco pela
    // direita, e sem ele o ícone inteiro encolheria para caber.
    private static DrawingImage Framed(Drawing content)
    {
        var square = new Rect(0, 0, Size, Size);
        var frame = new DrawingGroup();
        frame.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(square) });
        frame.Children.Add(content);
        return new DrawingImage(frame) { Viewbox = square };
    }

    // Os nomes de ícone que existem, para os testes conferirem a tabela contra os desenhos.
    public static IReadOnlyCollection<string> Known => Data.Value.Files.Keys;
    public static IReadOnlyCollection<string> KnownFolders => Data.Value.Folders.Keys;
    public static IEnumerable<string> Mapped => ByName.Values.Concat(ByExtension.Values).Distinct();
    public static IEnumerable<string> MappedFolders => Data.Value.FolderNames.Values.Distinct();
}
