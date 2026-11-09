using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius.Tests;

// O ícone de cada arquivo, como no VS Code com o Material Icon Theme.
public class FileIconsTests
{
    [Theory]
    [InlineData("index.php", "php")]
    [InlineData("INDEX.PHP", "php")]
    [InlineData("index.html", "html")]
    [InlineData("estilo.css", "css")]
    [InlineData("estilo.scss", "sass")]
    [InlineData("estilo.sass", "sass")]
    [InlineData("app.js", "javascript")]
    [InlineData("app.ts", "typescript")]
    [InlineData("App.tsx", "react_ts")]
    [InlineData("dados.json", "json")]
    [InlineData("composer.json", "json")]
    [InlineData("package.json", "nodejs")]
    [InlineData("notas.md", "markdown")]
    [InlineData("README.md", "readme")]
    [InlineData("LICENSE", "license")]
    [InlineData("leia.txt", "document")]
    [InlineData("foto.JPG", "image")]
    [InlineData("logo.svg", "svg")]
    [InlineData("index.blade.php", "laravel")]
    [InlineData(".env", "tune")]
    [InlineData(".env.local", "tune")]
    [InlineData(".gitignore", "git")]
    [InlineData(".htaccess", "xml")]
    [InlineData("Programa.cs", "csharp")]
    [InlineData("Sirius.csproj", "visualstudio")]
    [InlineData("instalar.bat", "console")]
    [InlineData("build.ps1", "powershell")]
    [InlineData("banco.sql", "database")]
    [InlineData("site.tar.gz", "zip")]
    public void Icone_de_cada_arquivo(string file, string icon) => Assert.Equal(icon, FileIcons.KeyFor(file));

    [Theory]
    [InlineData("Makefile")]
    [InlineData("arquivo")]
    [InlineData("nome.")]
    [InlineData("dados.xyz")]
    public void Arquivo_desconhecido_fica_sem_icone_proprio(string file) => Assert.Null(FileIcons.KeyFor(file));

    [Fact]
    public void Todo_icone_da_tabela_tem_desenho()
    {
        Assert.NotEmpty(FileIcons.Known);
        foreach (var icon in FileIcons.Mapped)
            Assert.Contains(icon, FileIcons.Known);
    }

    // Um caminho lido errado sai da caixa ou fica minúsculo: o desenho precisa ocupar o quadrado de 16.
    [AvaloniaFact]
    public void Todo_desenho_cabe_no_quadrado()
    {
        foreach (var key in FileIcons.Known)
        {
            var image = Assert.IsType<DrawingImage>(FileIcons.ForKey(key, Palette.Grafite));
            Assert.Equal(new Size(FileIcons.Size, FileIcons.Size), image.Size);
            var frame = Assert.IsType<DrawingGroup>(image.Drawing);
            var content = frame.Children[1].GetBounds();
            Assert.True(content.X >= -0.01 && content.Y >= -0.01 && content.Right <= 16.01 && content.Bottom <= 16.01, $"{key}: {content}");
            Assert.True(Math.Max(content.Width, content.Height) >= 8, $"{key}: {content}");
        }
    }

    [AvaloniaFact]
    public void Toml_tem_versao_para_o_tema_claro()
    {
        Assert.NotSame(FileIcons.ForKey("toml", Palette.Claro), FileIcons.ForKey("toml", Palette.Grafite));
        Assert.Same(FileIcons.ForKey("php", Palette.Claro), FileIcons.ForKey("php", Palette.Claro));
    }

    // Pastas, pelo nome, como no Material.

    [Theory]
    [InlineData("css", "folder-css")]
    [InlineData("CSS", "folder-css")]
    [InlineData("js", "folder-javascript")]
    [InlineData("img", "folder-images")]
    [InlineData("includes", "folder-include")]
    [InlineData("public", "folder-public")]
    [InlineData("src", "folder-src")]
    [InlineData("assets", "folder-resource")]
    [InlineData("node_modules", "folder-node")]
    [InlineData("components", "folder-components")]
    [InlineData(".vscode", "folder-vscode")]
    [InlineData(".github", "folder-github")]
    [InlineData("_includes", "folder-include")]
    [InlineData("__tests__", "folder-test")]
    public void Icone_de_cada_pasta(string folder, string icon) => Assert.Equal(icon, FileIcons.FolderKeyFor(folder));

    [Theory]
    [InlineData("minhas-coisas")]
    [InlineData("aula 3")]
    [InlineData("_")]
    public void Pasta_desconhecida_fica_com_a_pasta_comum(string folder) => Assert.Null(FileIcons.FolderKeyFor(folder));

    [Fact]
    public void Todo_nome_de_pasta_tem_desenho()
    {
        Assert.True(FileIcons.KnownFolders.Count > 100);
        foreach (var icon in FileIcons.MappedFolders)
            Assert.Contains(icon, FileIcons.KnownFolders);
    }

    [AvaloniaFact]
    public void Toda_pasta_cabe_no_quadrado_fechada_e_aberta()
    {
        foreach (var key in FileIcons.KnownFolders.Append(null))
        {
            foreach (var open in new[] { false, true })
            {
                var image = Assert.IsType<DrawingImage>(FileIcons.ForFolderKey(key, open, Palette.Grafite));
                Assert.True(image.Size == new Size(FileIcons.Size, FileIcons.Size), $"{key} {open}: {image.Size}");
                // Os desenhos das pastas do Sass e do Stylus passam um pouco da borda no próprio SVG: o Viewbox corta.
                var content = Assert.IsType<DrawingGroup>(image.Drawing).Children[1].GetBounds();
                Assert.True(content.X >= -0.01 && content.Y >= -0.01 && content.Right <= 17 && content.Bottom <= 17, $"{key} {open}: {content}");
                Assert.True(content.Width >= 12, $"{key} {open}: {content}");
            }
        }
        Assert.NotSame(FileIcons.ForFolderKey("folder-css", false, Palette.Grafite), FileIcons.ForFolderKey("folder-css", true, Palette.Grafite));
    }

    [AvaloniaFact]
    public void Arvore_e_abas_mostram_o_icone_de_cada_arquivo_e_pasta()
    {
        using var temp = new TempFolder();
        temp.File("index.php", "<?php echo 1;");
        temp.File("leia", "sem extensão");
        temp.File(Path.Combine("css", "estilo.css"), "a {}");
        Directory.CreateDirectory(Path.Combine(temp.Path, "aulas"));
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(FileIcons.ForFolder("css", open: false), window.TreeRows.Single(r => r.Name == "css").Icon);
        Assert.Same(FileIcons.ForFolderKey(null, false, Palette.Current), window.TreeRows.Single(r => r.Name == "aulas").Icon);
        Assert.Same(FileIcons.For("index.php"), window.TreeRows.Single(r => r.Name == "index.php").Icon);
        Assert.Same(FileIcons.ForKey(null, Palette.Current), window.TreeRows.Single(r => r.Name == "leia").Icon);

        // Abrir um arquivo de dentro abre a pasta na árvore, e o ícone dela também.
        window.OpenFile(Path.Combine(temp.Path, "css", "estilo.css"));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(FileIcons.ForFolder("css", open: true), window.TreeRows.Single(r => r.Name == "css").Icon);
        Assert.Same(FileIcons.For("estilo.css"), window.TabRows.Single().Icon);
        window.Close();
    }
}
