using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using Sirius.Core;
using Xunit;

namespace Sirius.Tests;

public class FindTests
{
    // A lógica, sem janela

    private static List<string> Found(string text, FindOptions options) =>
        FindInText.FindAll(text, options).Matches.Select(m => text.Substring(m.Offset, m.Length)).ToList();

    [Fact]
    public void Sem_diferenciar_maiusculas_acha_todas_as_formas()
    {
        Assert.Equal(["nome", "Nome", "NOME"], Found("nome Nome NOME", new("nome")));
    }

    [Fact]
    public void Diferenciando_maiusculas_acha_so_a_igual()
    {
        var result = FindInText.FindAll("nome Nome NOME", new("Nome", MatchCase: true));
        Assert.Equal([new FindMatch(5, 4)], result.Matches);
    }

    [Fact]
    public void Acentos_tambem_valem_sem_diferenciar_maiusculas()
    {
        Assert.Equal(["ação", "AÇÃO"], Found("ação AÇÃO acao", new("ação")));
    }

    [Fact]
    public void Palavra_inteira_nao_acha_dentro_de_outra_mas_acha_a_variavel_do_php()
    {
        Assert.Equal([0, 12], FindInText.FindAll("nome nomes $nome _nome", new("nome", WholeWord: true)).Matches.Select(m => m.Offset));
    }

    [Fact]
    public void Texto_comum_nao_e_expressao_regular()
    {
        Assert.Equal(["a.b"], Found("a.b axb", new("a.b")));
        Assert.Equal(["a.b", "axb"], Found("a.b axb", new("a.b", UseRegex: true)));
    }

    [Fact]
    public void Expressao_invalida_avisa_em_vez_de_quebrar()
    {
        var result = FindInText.FindAll("abc", new("(", UseRegex: true));
        Assert.True(result.Invalid);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Resultado_vazio_fica_de_fora()
    {
        Assert.Empty(FindInText.FindAll("a\nb", new("^", UseRegex: true)).Matches);
    }

    [Fact]
    public void Para_no_limite_e_avisa()
    {
        var result = FindInText.FindAll(new string('a', 50), new("a"), limit: 10);
        Assert.Equal(10, result.Matches.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Troca_com_grupos_e_quebra_de_linha_na_expressao_regular()
    {
        var text = "a=1;\r\nb=2;";
        var options = new FindOptions(@"(\w)=(\d)", UseRegex: true);
        var plan = FindInText.ReplaceAllPlan(text, options, @"$2:$1\n");
        Assert.Equal(["2:b\r\n", "1:a\r\n"], plan.Select(p => p.Text));
        Assert.Equal("1:a\r\n", FindInText.ReplacementFor(text, FindInText.FindAll(text, options).Matches[0], options, @"$2:$1\n"));
    }

    [Fact]
    public void Texto_comum_troca_ao_pe_da_letra()
    {
        var plan = FindInText.ReplaceAllPlan("x x", new("x"), "$1\\n");
        Assert.All(plan, p => Assert.Equal("$1\\n", p.Text));
        Assert.Equal([2, 0], plan.Select(p => p.Match.Offset));
    }

    [Fact]
    public void Proximo_e_anterior_dao_a_volta_no_arquivo()
    {
        FindMatch[] matches = [new(2, 1), new(5, 1), new(9, 1)];
        Assert.Equal(1, FindInText.IndexFrom(matches, 3));
        Assert.Equal(0, FindInText.IndexFrom(matches, 10));
        Assert.Equal(0, FindInText.IndexBefore(matches, 5));
        Assert.Equal(2, FindInText.IndexBefore(matches, 2));
        Assert.Equal(1, FindInText.IndexOf(matches, 5, 1));
        Assert.Equal(-1, FindInText.IndexOf(matches, 5, 2));
    }

    // Na janela

    private static TextEditor Editor(MainWindow window) => window.FindControl<TextEditor>("CodeEditor")!;

    private static MainWindow OpenWith(TempFolder temp, string name, string text, int caret)
    {
        var path = temp.File(name, text);
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        Editor(window).TextArea.Caret.Offset = caret;
        Editor(window).TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string Selected(MainWindow window) => Editor(window).TextArea.Selection.GetText();
    private static int SelectionStart(MainWindow window) => Editor(window).TextArea.Selection.SurroundingSegment.Offset;

    private static void Press(MainWindow window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Ctrl_F_abre_com_a_palavra_sob_o_cursor_e_conta()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.php", "<?php\n$nome = 1;\necho $nome;\necho $nome . $Nome;\n", 8);

        Press(window, PhysicalKey.F, RawInputModifiers.Control);

        Assert.True(window.IsFindOpen);
        Assert.False(window.IsFindReplaceShown);
        Assert.Equal("$nome", window.FindText);
        Assert.Equal(4, window.FindMatches.Count);
        Assert.Equal("1 de 4", window.FindStatusText);
        Assert.Equal(6, SelectionStart(window));
        window.Close();
    }

    [AvaloniaFact]
    public void F3_e_Shift_F3_andam_e_dao_a_volta()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "um dois um tres um", 0);
        window.OpenFind();
        window.FindText = "um";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, SelectionStart(window));

        Editor(window).TextArea.Focus();
        Press(window, PhysicalKey.F3);
        Assert.Equal(8, SelectionStart(window));
        Assert.Equal("2 de 3", window.FindStatusText);
        Press(window, PhysicalKey.F3);
        Press(window, PhysicalKey.F3);
        Assert.Equal(0, SelectionStart(window));
        Press(window, PhysicalKey.F3, RawInputModifiers.Shift);
        Assert.Equal(16, SelectionStart(window));
        Assert.Equal("3 de 3", window.FindStatusText);
        window.Close();
    }

    [AvaloniaFact]
    public void F3_com_a_barra_fechada_reabre_com_a_ultima_busca_sem_tirar_o_foco_do_codigo()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "um dois um", 0);
        window.OpenFind();
        window.FindText = "dois";
        Press(window, PhysicalKey.Escape);
        Assert.False(window.IsFindOpen);

        Editor(window).TextArea.Caret.Offset = 0;
        Editor(window).TextArea.ClearSelection();
        Editor(window).TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        Press(window, PhysicalKey.F3);

        Assert.True(window.IsFindOpen);
        Assert.Equal("dois", Selected(window));
        Assert.True(Editor(window).TextArea.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_na_caixa_vai_ao_proximo_e_Esc_fecha_e_volta_ao_codigo()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "a b a b a", 0);
        Press(window, PhysicalKey.F, RawInputModifiers.Control);
        Assert.Equal("a", window.FindText);
        Assert.True(window.FindControl<TextBox>("FindInput")!.IsFocused);

        Press(window, PhysicalKey.Enter);
        Assert.Equal(4, SelectionStart(window));
        Press(window, PhysicalKey.Enter, RawInputModifiers.Shift);
        Assert.Equal(0, SelectionStart(window));

        Press(window, PhysicalKey.Escape);
        Assert.False(window.IsFindOpen);
        Assert.True(Editor(window).TextArea.IsKeyboardFocusWithin);
        Assert.Empty(window.FindMatches);
        window.Close();
    }

    [AvaloniaFact]
    public void Diferenciar_maiusculas_e_palavra_inteira_mudam_a_contagem()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "Nome nome nomes", 0);
        window.OpenFind();
        window.FindText = "nome";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, window.FindMatches.Count);

        Press(window, PhysicalKey.C, RawInputModifiers.Alt);
        Assert.True(window.FindMatchCase);
        Assert.Equal(2, window.FindMatches.Count);

        Press(window, PhysicalKey.W, RawInputModifiers.Alt);
        Assert.Single(window.FindMatches);
        Assert.Equal(5, SelectionStart(window));
        Assert.Equal("1 de 1", window.FindStatusText);
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_H_mostra_o_substituir_e_troca_um_por_um()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "gato gato gato", 0);
        Press(window, PhysicalKey.H, RawInputModifiers.Control);
        Assert.True(window.IsFindReplaceShown);
        Assert.Equal("gato", window.FindText);
        Assert.True(window.FindControl<TextBox>("FindReplaceInput")!.IsFocused);

        window.FindReplacement = "cão";
        Press(window, PhysicalKey.Enter);
        Assert.Equal("cão gato gato", window.ActiveDocument!.Document.Text);
        Assert.Equal(4, SelectionStart(window));
        Assert.Equal("1 de 2", window.FindStatusText);

        // A seleção que não é um resultado só leva ao próximo, sem trocar nada.
        Editor(window).TextArea.ClearSelection();
        Editor(window).TextArea.Caret.Offset = 0;
        window.ReplaceOne();
        Assert.Equal("cão gato gato", window.ActiveDocument!.Document.Text);
        Assert.Equal(4, SelectionStart(window));

        window.ReplaceOne();
        window.ReplaceOne();
        Assert.Equal("cão cão cão", window.ActiveDocument!.Document.Text);
        Assert.Equal("Nenhum resultado", window.FindStatusText);
        window.Close();
    }

    [AvaloniaFact]
    public void Trocar_por_algo_que_contem_a_busca_nao_entra_em_laco()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "a a", 0);
        window.OpenFind(replace: true);
        window.FindText = "a";
        window.FindReplacement = "aa";

        window.ReplaceOne();
        window.ReplaceOne();

        Assert.Equal("aa aa", window.ActiveDocument!.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Substituir_tudo_respeita_as_opcoes_e_um_Ctrl_Z_desfaz_tudo()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "Nome nome nomes nome", 0);
        window.OpenFind(replace: true);
        window.FindText = "nome";
        window.SetFindOptions(matchCase: true, wholeWord: true, useRegex: false);
        window.FindReplacement = "x";

        Assert.Equal(2, window.ReplaceAll());
        Assert.Equal("Nome x nomes x", window.ActiveDocument!.Document.Text);
        Assert.Contains("2 trocas", window.StatusText);

        window.ActiveDocument!.Document.UndoStack.Undo();
        Assert.Equal("Nome nome nomes nome", window.ActiveDocument!.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_Alt_Enter_substitui_tudo_com_os_grupos_da_expressao()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "$a = 1;\n$b = 2;", 0);
        window.OpenFind(replace: true);
        window.FindText = @"\$(\w) = (\d)";
        window.SetFindOptions(matchCase: false, wholeWord: false, useRegex: true);
        window.FindReplacement = "$$$1 = $2$2";
        window.FindControl<TextBox>("FindReplaceInput")!.Focus();
        Dispatcher.UIThread.RunJobs();

        Press(window, PhysicalKey.Enter, RawInputModifiers.Control | RawInputModifiers.Alt);

        Assert.Equal("$a = 11;\n$b = 22;", window.ActiveDocument!.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Expressao_invalida_aparece_na_contagem()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "abc", 0);
        window.OpenFind();
        window.SetFindOptions(matchCase: false, wholeWord: false, useRegex: true);
        window.FindText = "(";

        Assert.Equal("Expressão inválida", window.FindStatusText);
        Assert.Equal(0, window.ReplaceAll());
        window.Close();
    }

    [AvaloniaFact]
    public void Digitar_no_codigo_atualiza_a_contagem()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "ola", 3);
        window.OpenFind();
        window.FindText = "ola";
        Assert.Single(window.FindMatches);

        window.ActiveDocument!.Document.Insert(3, " ola");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, window.FindMatches.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Trocar_de_aba_procura_no_arquivo_novo_e_na_imagem_a_barra_fecha()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "x", 0);
        var other = temp.File("b.txt", "x x x");
        window.OpenFind();
        window.FindText = "x";
        Assert.Single(window.FindMatches);

        window.OpenFile(other);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsFindOpen);
        Assert.Equal(3, window.FindMatches.Count);

        var image = temp.File("c.png");
        File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
        window.OpenFile(image);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsFindOpen);

        // Na imagem, Ctrl F não tem onde procurar.
        Press(window, PhysicalKey.F, RawInputModifiers.Control);
        Assert.False(window.IsFindOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void A_barra_de_comando_tem_procurar_e_substituir()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "x", 0);

        var commands = window.PaletteCommandNames("").ToList();

        Assert.Contains("Procurar no arquivo", commands);
        Assert.Contains("Substituir no arquivo", commands);
        Assert.Contains("Próximo resultado", commands);
        Assert.Contains("Resultado anterior", commands);
        window.Close();
    }
}
