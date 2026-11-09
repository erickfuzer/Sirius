using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius.Tests;

public class ChangeBlocksTests
{
    // Sem janela

    [Fact]
    public void Acha_trecho_mudado_novo_e_apagado()
    {
        var blocks = ChangeBlocks.Find("um\ndois\ntres\nquatro\ncinco", "um\nDOIS\ntres\ncinco\nseis");

        Assert.Equal(3, blocks.Count);
        Assert.Equal((2, 1), (blocks[0].First, blocks[0].Count));
        Assert.Equal(["dois"], blocks[0].OldLines);
        Assert.True(blocks[1].IsDeleted);
        Assert.Equal(["quatro"], blocks[1].OldLines);
        Assert.Equal(4, blocks[1].MarkLine);
        Assert.True(blocks[2].IsAdded);
        Assert.Equal((5, 1), (blocks[2].First, blocks[2].Count));
    }

    [Theory]
    [InlineData("um\ndois\ntres", "um\nDOIS\ntres")]
    [InlineData("um\ndois\ntres", "um\ntres")]
    [InlineData("um\ndois\ntres", "dois\ntres")]
    [InlineData("um\ndois\ntres", "um\ndois")]
    [InlineData("um\ndois\ntres\n", "um\ndois\n")]
    [InlineData("um\ndois", "um\ndois\ntres\nquatro")]
    [InlineData("um\ndois\n", "zero\num\ndois\n")]
    [InlineData("um", "")]
    [InlineData("", "um\ndois")]
    [InlineData("a\nb\nc\nd\ne", "a\nB\nc\nE")]
    public void Desfazer_tudo_volta_ao_commit(string commit, string now)
    {
        Assert.Equal(commit, ChangeBlocks.Revert(now, ChangeBlocks.Find(commit, now)));
    }

    [Fact]
    public void Desfazer_tudo_volta_ao_commit_em_muitos_casos()
    {
        var random = new Random(7);
        string[] words = ["a", "b", "c", "d", "e", ""];
        for (var round = 0; round < 400; round++)
        {
            var commit = Enumerable.Range(0, random.Next(0, 9)).Select(_ => words[random.Next(words.Length)]).ToList();
            var now = commit.ToList();
            for (var edit = random.Next(1, 4); edit > 0; edit--)
            {
                var at = random.Next(now.Count + 1);
                switch (random.Next(3))
                {
                    case 0: now.Insert(at, words[random.Next(words.Length)] + "x"); break;
                    case 1 when at < now.Count: now.RemoveAt(at); break;
                    case 2 when at < now.Count: now[at] += "y"; break;
                }
            }
            var a = string.Join('\n', commit);
            var b = string.Join('\n', now);
            Assert.Equal(a, ChangeBlocks.Revert(b, ChangeBlocks.Find(a, b)));
        }
    }

    [Fact]
    public void Desfazer_um_trecho_deixa_os_outros()
    {
        var commit = "um\ndois\ntres\nquatro";
        var now = "UM\ndois\ntres\nQUATRO";
        var blocks = ChangeBlocks.Find(commit, now);

        Assert.Equal("UM\ndois\ntres\nquatro", ChangeBlocks.Revert(now, [blocks[1]]));
        Assert.Equal("um\ndois\ntres\nQUATRO", ChangeBlocks.Revert(now, [blocks[0]]));
    }

    [Fact]
    public void Mantem_a_quebra_de_linha_do_arquivo()
    {
        var commit = "um\r\ndois\r\ntres\r\n";
        var now = "um\r\ntres\r\n";
        Assert.Equal(commit, ChangeBlocks.Revert(now, ChangeBlocks.Find(commit, now)));
    }

    [Fact]
    public void Linha_da_marca_e_selecao()
    {
        var blocks = ChangeBlocks.Find("a\nb\nc\nd\ne\nf", "a\nB\nC\nd\nf");
        // Trocou b e c; apagou e, logo acima do f.
        Assert.Equal(0, ChangeBlocks.IndexAt(blocks, 3));
        Assert.Equal(1, ChangeBlocks.IndexAt(blocks, 5));
        Assert.Equal(-1, ChangeBlocks.IndexAt(blocks, 4));
        Assert.True(blocks[0].Touches(1, 2));
        Assert.False(blocks[0].Touches(4, 4));
        Assert.True(blocks[1].Touches(5, 5));
        Assert.False(blocks[1].Touches(4, 4));
    }

    [Fact]
    public void Linhas_antigas_de_php_tem_as_cores_do_php()
    {
        var excerpt = new AvaloniaEdit.Document.TextDocument("$nome = 'Ana';");
        var full = "<?php\n$nome = 'Ana';\n";

        // Sozinha, a linha fica fora do <?php e vira texto de HTML, sem cor.
        Assert.DoesNotContain(SyntaxHighlighter.ForDocument("a.php", excerpt)!.TokensFor(1), token => token.Kind == TokenKind.String);
        // Lida a partir do arquivo antigo, continua dentro do PHP.
        Assert.Contains(SyntaxHighlighter.ForExcerpt("a.php", excerpt, full, 2)!.TokensFor(1), token => token.Kind == TokenKind.String);
    }

    // Na janela, com Git de verdade

    private static async Task Until(Func<bool> condition, int seconds = 30)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(15);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<MainWindow> OpenCommitted(TempFolder temp, TempFolder config, string path)
    {
        var git = await GitClientTests.Repository(temp);
        await git.RunAsync(["add", "-A"]);
        Assert.True((await git.RunAsync(["commit", "-m", "base"])).Ok);
        var window = new MainWindow(new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.GitEnvironment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(config.Path, "gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.RefreshGitAsync();
        window.OpenFile(path);
        await window.UpdateGitMarksAsync();
        return window;
    }

    [AvaloniaFact]
    public async Task Mostra_como_era_e_desfaz_so_o_trecho()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("index.php", "<?php\n$nome = 'Ana';\necho $nome;\n$idade = 30;\n");
        var window = await OpenCommitted(temp, config, path);
        var document = window.ActiveDocument!.Document;
        document.Text = "<?php\n$nome = 'Bia';\necho $nome;\n$idade = 31;\n";
        await Until(() => window.ChangeBlockList.Count == 2);

        Assert.True(window.ShowChangeAt(2));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsChangePeekOpen);
        Assert.Equal("$nome = 'Ana';", window.ChangePeekOldText);
        Assert.Contains("Como era antes", window.ChangePeekTitleText);
        Assert.Contains("trecho 1 de 2", window.ChangePeekTitleText);

        window.MoveToChange(1);
        Assert.Equal("$idade = 30;", window.ChangePeekOldText);
        Assert.Contains("trecho 2 de 2", window.ChangePeekTitleText);
        window.MoveToChange(1);
        Assert.Equal("$nome = 'Ana';", window.ChangePeekOldText);

        window.RevertShownChange();
        Assert.False(window.IsChangePeekOpen);
        Assert.Equal("<?php\n$nome = 'Ana';\necho $nome;\n$idade = 31;\n", document.Text);
        Assert.True(window.ActiveDocument!.IsDirty);
        await Until(() => window.ChangeBlockList.Count == 1);

        // Um Ctrl Z traz tudo de volta.
        document.UndoStack.Undo();
        Assert.Equal("<?php\n$nome = 'Bia';\necho $nome;\n$idade = 31;\n", document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Linhas_apagadas_e_novas_tem_o_proprio_aviso()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.css", "a {}\nb {}\nc {}\n");
        var window = await OpenCommitted(temp, config, path);
        var document = window.ActiveDocument!.Document;
        document.Text = "a {}\nc {}\nd {}\n";
        await Until(() => window.ChangeBlockList.Count == 2);

        Assert.True(window.ShowChangeAt(2));
        Assert.Contains("Linhas apagadas", window.ChangePeekTitleText);
        Assert.Equal("b {}", window.ChangePeekOldText);

        Assert.True(window.ShowChangeAt(3));
        Assert.Contains("Linhas novas", window.ChangePeekTitleText);
        Assert.Equal("", window.ChangePeekOldText);
        Assert.Contains("não existia", window.ChangePeekNoteText);

        window.RevertShownChange();
        Assert.Equal("a {}\nc {}\n", document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Desfaz_as_mudancas_da_selecao()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "um\ndois\ntres\nquatro\ncinco\n");
        var window = await OpenCommitted(temp, config, path);
        var document = window.ActiveDocument!.Document;
        document.Text = "UM\ndois\nTRES\nquatro\nCINCO\n";
        await Until(() => window.ChangeBlockList.Count == 3);

        // Da linha 1 até o meio da 3: pega o UM e o TRES, mas não o CINCO.
        window.SelectForTests(0, document.GetLineByNumber(3).Offset + 2);
        window.RevertSelectedChanges();

        Assert.Equal("um\ndois\ntres\nquatro\nCINCO\n", document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Digitar_ou_esc_fecham_o_quadro()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "um\ndois\n");
        var window = await OpenCommitted(temp, config, path);
        var document = window.ActiveDocument!.Document;
        document.Text = "um\nDOIS\n";
        await Until(() => window.ChangeBlockList.Count == 1);

        window.ShowChangeAt(2);
        document.Insert(0, "x");
        Assert.False(window.IsChangePeekOpen);

        // As duas linhas mudadas, vizinhas, viram um trecho só.
        await Until(() => window.ChangeBlockList is [{ First: 1, Count: 2 }]);
        Assert.True(window.ShowChangeAt(2));
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsChangePeekOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clique_na_faixa_da_margem_abre_o_quadro_abaixo_do_trecho()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "um\ndois\ntres\n");
        var window = await OpenCommitted(temp, config, path);
        window.ActiveDocument!.Document.Text = "um\nDOIS\ntres\n";
        await Until(() => window.ChangeBlockList.Count == 1);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var gutter = window.GetVisualDescendants().OfType<GutterMargin>().Single();
        var view = gutter.TextView!;
        var lineHeight = view.DefaultLineHeight;
        // A faixa fica perto da borda direita da margem, na altura da linha 2.
        var point = gutter.TranslatePoint(new Point(gutter.Bounds.Width - 6, lineHeight * 1.5), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsChangePeekOpen);
        Assert.Equal("dois", window.ChangePeekOldText);
        // Logo abaixo da linha 2: o topo do quadro fica depois de duas linhas.
        var lineTwoBottom = view.TranslatePoint(new Point(0, lineHeight * 2), window)!.Value.Y;
        var peekTop = window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "ChangePeek")
            .TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.InRange(peekTop, lineTwoBottom - 1, lineTwoBottom + 6);
        window.Close();
    }
}
