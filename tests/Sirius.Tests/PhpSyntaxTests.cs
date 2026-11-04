using System.Diagnostics;
using System.Text;
using AvaloniaEdit.Document;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius.Tests;

// Um .php é HTML com trechos de PHP, então cada parte precisa ter a cor da sua linguagem.
public class PhpSyntaxTests
{
    private static List<(string Text, TokenKind Kind)> Line(string source, int lineNumber)
    {
        var document = new TextDocument(source);
        var highlighter = SyntaxHighlighter.ForDocument("pagina.php", document);
        Assert.NotNull(highlighter);
        var text = document.GetText(document.GetLineByNumber(lineNumber));
        return highlighter.TokensFor(lineNumber).Select(token => (text.Substring(token.Start, token.Length), token.Kind)).ToList();
    }

    private static bool HasComment(List<(string Text, TokenKind Kind)> tokens) =>
        tokens.Any(token => token.Kind == TokenKind.Comment);

    // O defeito original

    [Fact]
    public void Cor_em_style_nao_vira_comentario()
    {
        var tokens = Line("<?php $a = 1; ?>\n<style>\n  h1 { color: #2f7a3b; }\n</style>\n", 3);

        Assert.False(HasComment(tokens));
        // A propriedade com cor de propriedade prova que o trecho foi lido como CSS.
        Assert.Contains(("color", TokenKind.Type), tokens);
    }

    [Fact]
    public void Texto_com_cerquilha_fora_do_php_nao_e_comentario() =>
        Assert.False(HasComment(Line("<p># titulo</p>\n", 1)));

    [Fact]
    public void Link_para_ancora_e_texto_de_atributo()
    {
        var tokens = Line("<a href=\"#add\">x</a>\n", 1);

        Assert.False(HasComment(tokens));
        Assert.Contains(tokens, token => token.Text.Contains("add") && token.Kind == TokenKind.String);
    }

    // HTML

    [Fact]
    public void Tags_e_atributos_tem_cor_em_arquivo_sem_php()
    {
        var tokens = Line("<div class=\"cartao\">Olá</div>\n", 1);

        Assert.Contains(("div", TokenKind.Type), tokens);
        Assert.Contains(("class", TokenKind.Function), tokens);
        Assert.Contains(tokens, token => token.Text.Contains("cartao") && token.Kind == TokenKind.String);
    }

    [Fact]
    public void Comentario_de_html_e_comentario() =>
        Assert.Contains(Line("<!-- nota -->\n", 1), token => token.Text.Contains("nota") && token.Kind == TokenKind.Comment);

    [Fact]
    public void Javascript_em_script_tem_cor_de_javascript()
    {
        var tokens = Line("<script>\n  const total = 1; // conta\n</script>\n", 2);

        Assert.Contains(("const", TokenKind.Keyword), tokens);
        Assert.Contains(tokens, token => token.Text.Contains("conta") && token.Kind == TokenKind.Comment);
    }

    // PHP

    [Fact]
    public void Codigo_php_continua_com_as_cores_de_php()
    {
        const string Source = "<?php\n\nfunction soma(int $a): int\n{\n    return $a + 1; // conta\n}\n";

        Assert.Contains(("function", TokenKind.Keyword), Line(Source, 3));
        Assert.Contains(("soma", TokenKind.Function), Line(Source, 3));
        var body = Line(Source, 5);
        Assert.Contains(("return", TokenKind.Keyword), body);
        Assert.Contains(body, token => token.Text.Contains("conta") && token.Kind == TokenKind.Comment);
    }

    [Fact]
    public void Dentro_do_php_cerquilha_continua_sendo_comentario() =>
        Assert.True(HasComment(Line("<?php\n# nota\n$a = 1;\n", 2)));

    [Fact]
    public void Comentario_de_varias_linhas_em_php()
    {
        const string Source = "<?php\n/* primeira\n   segunda */\n$a = 1;\n";

        Assert.All(Line(Source, 3), token => Assert.Equal(TokenKind.Comment, token.Kind));
        Assert.False(HasComment(Line(Source, 4)));
    }

    [Fact]
    public void Php_e_html_se_alternam_na_mesma_linha()
    {
        var tokens = Line("<?php if ($ok): ?><b class=\"x\"><?= $nome ?></b><?php endif; ?>\n", 1);

        Assert.Contains(("if", TokenKind.Keyword), tokens);
        Assert.Contains(("b", TokenKind.Type), tokens);
        Assert.Contains(("class", TokenKind.Function), tokens);
        Assert.Contains(("endif", TokenKind.Keyword), tokens);
    }

    [Fact]
    public void Depois_de_fechar_o_php_volta_a_ser_html()
    {
        const string Source = "<?php\n$a = 1;\n?>\n<p># texto</p>\n<?php\n# nota\n";

        Assert.False(HasComment(Line(Source, 4)));
        Assert.True(HasComment(Line(Source, 6)));
    }

    [Fact]
    public void Php_dentro_de_um_atributo_mantem_o_estado_seguinte()
    {
        const string Source = "<a href=\"<?= $url ?>\" class=\"x\">y</a>\n<p>z</p>\n";

        Assert.Contains(("class", TokenKind.Function), Line(Source, 1));
        Assert.Contains(("p", TokenKind.Type), Line(Source, 2));
    }

    [Fact]
    public void Editar_refaz_as_cores_das_linhas_seguintes()
    {
        var document = new TextDocument("<p>a</p>\n# b\n");
        var highlighter = SyntaxHighlighter.ForDocument("pagina.php", document)!;
        Assert.DoesNotContain(highlighter.TokensFor(2), token => token.Kind == TokenKind.Comment);

        document.Insert(0, "<?php\n");

        Assert.Contains(highlighter.TokensFor(3), token => token.Kind == TokenKind.Comment);
    }

    // Velocidade: a gramática nova carrega HTML, CSS, JavaScript e PHP.

    [Fact]
    public void Arquivo_grande_continua_rapido()
    {
        var source = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            source.Append("<div class=\"a\" style=\"color: #fff\"><?php echo $nome[").Append(i).Append("]; ?></div>\n");
            source.Append("<script>const a = ").Append(i).Append(";</script>\n");
        }
        var document = new TextDocument(source.ToString());
        var highlighter = SyntaxHighlighter.ForDocument("grande.php", document)!;

        var clock = Stopwatch.StartNew();
        highlighter.TokensFor(document.LineCount);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Levou {clock.ElapsedMilliseconds} ms para {document.LineCount} linhas.");
    }
}
