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

    [AvaloniaFact]
    public void Arvore_e_abas_mostram_o_icone_so_nos_arquivos()
    {
        using var temp = new TempFolder();
        temp.File("index.php", "<?php echo 1;");
        temp.File("leia", "sem extensão");
        Directory.CreateDirectory(Path.Combine(temp.Path, "css"));
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(window.TreeRows.Single(r => r.Name == "css").Icon);
        Assert.Same(FileIcons.For("index.php"), window.TreeRows.Single(r => r.Name == "index.php").Icon);
        Assert.Same(FileIcons.ForKey(null, Palette.Current), window.TreeRows.Single(r => r.Name == "leia").Icon);

        window.OpenFile(Path.Combine(temp.Path, "index.php"));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(FileIcons.For("index.php"), window.TabRows.Single().Icon);
        window.Close();
    }
}
