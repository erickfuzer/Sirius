using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sirius.Core;
using Xunit;

namespace Sirius.Tests;

public class SplitTests
{
    private const string Html = "<!doctype html>\n<html><body><h1>Oi</h1></body></html>\n";
    private const string Css = "h1 {\n  color: red;\n}\n";

    private static (MainWindow Window, string Html, string Css) Open(TempFolder temp)
    {
        var html = temp.File("index.html", Html);
        var css = temp.File("style.css", Css);
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(html);
        Dispatcher.UIThread.RunJobs();
        return (window, html, css);
    }

    private static void Type(MainWindow window, string text)
    {
        window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
        foreach (var ch in text)
            window.KeyTextInput(ch.ToString());
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Dividir_abre_o_mesmo_arquivo_a_direita_e_a_aba_clicada_vai_para_la()
    {
        using var temp = new TempFolder();
        var (window, html, css) = Open(temp);
        Assert.False(window.IsSplit);
        Assert.True(window.IsSplitButtonVisible);

        window.ToggleSplit();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsSplit);
        Assert.True(window.IsRightSideFocused);
        Assert.Equal(html, window.LeftDocument!.FullPath);
        Assert.Equal(html, window.RightDocument!.FullPath);
        // O mesmo arquivo dos dois lados é o mesmo texto: o que muda num aparece no outro.
        Assert.Same(window.LeftEditor.Document, window.RightEditor.Document);

        window.OpenFile(css);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(html, window.LeftDocument!.FullPath);
        Assert.Equal(css, window.RightDocument!.FullPath);
        Assert.Equal(css, window.ActiveDocument!.FullPath);
        Assert.True(window.LeftEditor.IsVisible);
        Assert.True(window.RightEditor.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_barra_invertida_divide_e_fecha()
    {
        using var temp = new TempFolder();
        var (window, _, _) = Open(temp);

        window.KeyPressQwerty(PhysicalKey.Backslash, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsSplit);
        Assert.Contains("Fechar o lado direito", window.PaletteCommandNames("lado"));
        Assert.Contains("Ir para o outro lado", window.PaletteCommandNames("lado"));

        window.KeyPressQwerty(PhysicalKey.Backslash, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsSplit);
        Assert.False(window.IsRightSideFocused);
        Assert.Contains("Dividir o editor: dois arquivos lado a lado", window.PaletteCommandNames("lado"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task O_lado_com_o_foco_e_o_editor_digitar_e_salvar_valem_para_ele()
    {
        using var temp = new TempFolder();
        var (window, html, css) = Open(temp);
        window.ToggleSplit();
        window.OpenFile(css);
        Dispatcher.UIThread.RunJobs();

        window.RightEditor.TextArea.Caret.Offset = 4;
        window.RightEditor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        Type(window, "/* fim */");
        Assert.Equal("h1 {/* fim */\n  color: red;\n}\n", window.RightDocument!.Document.Text);
        Assert.Equal(Html, window.LeftDocument!.Document.Text);

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("h1 {/* fim */\n  color: red;\n}\n", File.ReadAllText(css));
        Assert.Equal(Html, File.ReadAllText(html));

        // Clicar no lado esquerdo o torna "o" editor: a aba destacada e o Ctrl S passam a ser dele.
        window.LeftEditor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsRightSideFocused);
        Assert.Equal(html, window.ActiveDocument!.FullPath);
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, FontWeight(window, "index.html"));
        window.Close();
    }

    private static Avalonia.Media.FontWeight FontWeight(MainWindow window, string name) =>
        window.TabRows.Single(t => t.Name == name).Weight;

    [AvaloniaFact]
    public void Pares_e_comentario_funcionam_no_lado_direito()
    {
        using var temp = new TempFolder();
        var (window, _, css) = Open(temp);
        window.ToggleSplit();
        window.OpenFile(css);
        // No fim da linha: como no VS Code, o par não fecha na frente de uma letra.
        window.RightEditor.TextArea.Caret.Offset = 4;
        window.RightEditor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("(");
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("h1 {()\n", window.RightDocument!.Document.Text);

        window.ToggleComment();
        Assert.StartsWith("/* h1 {() */\n", window.RightDocument!.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void A_busca_procura_no_lado_com_o_foco()
    {
        using var temp = new TempFolder();
        var (window, _, css) = Open(temp);
        window.ToggleSplit();
        window.OpenFile(css);
        Dispatcher.UIThread.RunJobs();

        window.OpenFind();
        window.FindText = "red";
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.FindMatches);
        Assert.Equal("red", window.RightEditor.TextArea.Selection.GetText());

        // No lado esquerdo, o HTML não tem "red".
        window.FocusOtherSide();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(window.FindMatches);
        window.Close();
    }

    [AvaloniaFact]
    public void Fechar_a_aba_do_lado_direito_sem_o_foco_fecha_a_divisao()
    {
        using var temp = new TempFolder();
        var (window, html, css) = Open(temp);
        window.ToggleSplit();
        window.OpenFile(css);
        window.FocusOtherSide();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(html, window.ActiveDocument!.FullPath);

        Assert.True(window.CloseDocumentAsync(window.Documents.Single(d => d.FullPath == css)).Result);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsSplit);
        Assert.Equal(html, window.ActiveDocument!.FullPath);
        window.Close();
    }

    [AvaloniaFact]
    public void Fechar_a_aba_do_lado_com_o_foco_mostra_outra_ali()
    {
        using var temp = new TempFolder();
        var (window, html, css) = Open(temp);
        var js = temp.File("app.js", "let a = 1;");
        window.OpenFile(js);
        window.OpenFile(html);
        window.ToggleSplit();
        window.OpenFile(css);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.CloseDocumentAsync(window.ActiveDocument!).Result);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsSplit);
        Assert.True(window.IsRightSideFocused);
        Assert.NotEqual(css, window.RightDocument!.FullPath);
        Assert.Equal(html, window.LeftDocument!.FullPath);
        window.Close();
    }

    [AvaloniaFact]
    public void Abrir_uma_imagem_no_lado_direito_fecha_a_divisao_e_mostra_a_imagem()
    {
        using var temp = new TempFolder();
        var (window, html, _) = Open(temp);
        var image = temp.File("foto.png");
        File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
        window.ToggleSplit();
        Dispatcher.UIThread.RunJobs();

        window.OpenFile(image);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsSplit);
        Assert.True(window.IsImageShown);
        Assert.Equal(image, window.ActiveDocument!.FullPath);
        Assert.False(window.IsSplitButtonVisible);

        window.OpenFile(html);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.EditorVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Abrir_ao_lado_pela_lista_de_arquivos()
    {
        using var temp = new TempFolder();
        var (window, html, css) = Open(temp);

        window.OpenFileToSide(css);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsSplit);
        Assert.Equal(html, window.LeftDocument!.FullPath);
        Assert.Equal(css, window.RightDocument!.FullPath);
        window.Close();
    }

    [AvaloniaFact]
    public void A_fonte_e_a_quebra_de_linha_valem_para_os_dois_lados()
    {
        using var temp = new TempFolder();
        var (window, _, css) = Open(temp);
        window.ToggleSplit();
        window.OpenFile(css);

        window.SetEditorFontSize(18);
        window.SetWordWrap(true);

        Assert.Equal(18, window.LeftEditor.FontSize);
        Assert.Equal(18, window.RightEditor.FontSize);
        Assert.True(window.LeftEditor.WordWrap && window.RightEditor.WordWrap);
        window.Close();
    }
}
