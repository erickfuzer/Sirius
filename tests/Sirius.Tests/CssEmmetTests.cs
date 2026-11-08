using Sirius.Core;

namespace Sirius.Tests;

// O Emmet de CSS, conferido contra o que o próprio Emmet (e o VS Code) escreve.
public class CssEmmetTests
{
    private static string? Out(string abbreviation) => CssEmmet.Expand(abbreviation) is { } snippet ? Snippet.Parse(snippet).Text : null;

    [Theory]
    [InlineData("m10", "margin: 10px;")]
    [InlineData("m10-20", "margin: 10px 20px;")]
    [InlineData("m-10", "margin: -10px;")]
    [InlineData("m10--20", "margin: 10px -20px;")]
    [InlineData("m0", "margin: 0;")]
    [InlineData("m:a", "margin: auto;")]
    [InlineData("p10p", "padding: 10%;")]
    [InlineData("w100p", "width: 100%;")]
    [InlineData("h2e", "height: 2em;")]
    [InlineData("w1.5", "width: 1.5em;")]
    [InlineData("fz16", "font-size: 16px;")]
    [InlineData("lh1.5", "line-height: 1.5;")]
    [InlineData("op.5", "opacity: 0.5;")]
    [InlineData("z10", "z-index: 10;")]
    [InlineData("poa", "position: absolute;")]
    [InlineData("pos:a", "position: absolute;")]
    [InlineData("df", "display: flex;")]
    [InlineData("d:f", "display: flex;")]
    [InlineData("dib", "display: inline-block;")]
    [InlineData("dn", "display: none;")]
    [InlineData("tac", "text-align: center;")]
    [InlineData("c#f00", "color: #f00;")]
    [InlineData("c#f", "color: #fff;")]
    [InlineData("c#e0", "color: #e0e0e0;")]
    [InlineData("bd1-s#000", "border: 1px solid #000;")]
    [InlineData("m10!", "margin: 10px !important;")]
    [InlineData("m10+p5", "margin: 10px;\npadding: 5px;")]
    [InlineData("d", "display: block;")]
    [InlineData("bd", "border: 1px solid #000;")]
    [InlineData("b", "bottom: ;")]
    public void Expande_como_o_emmet(string abbreviation, string expected) => Assert.Equal(expected, Out(abbreviation));

    [Fact]
    public void Varias_opcoes_vem_num_campo_para_trocar()
    {
        var display = Snippet.Parse(CssEmmet.Expand("d")!);
        Assert.Equal("block", display.Text.Substring(display.Stops[0].Offset, display.Stops[0].Length));
    }

    [Fact]
    public void Trecho_pronto_tem_campos()
    {
        Assert.Equal("@media ${1:screen} {\n\t${0}\n}", CssEmmet.Expand("@m"));
        Assert.Equal("background-image: linear-gradient(${0});", CssEmmet.Expand("lg"));
    }

    [Theory]
    [InlineData("displ")]
    [InlineData("color")]
    [InlineData("top")]
    [InlineData("xyz")]
    [InlineData("10")]
    [InlineData("m10)")]
    public void Nao_vira_nada(string abbreviation) => Assert.Null(CssEmmet.Expand(abbreviation));

    // Onde vale

    private static string? At(string marked, string file = "a.css")
    {
        var caret = marked.IndexOf('|');
        return CssEmmet.TryExpandAt(marked.Remove(caret, 1), caret, file)?.Snippet;
    }

    [Theory]
    [InlineData("a {\n  m10|\n}", "margin: 10px;")]
    [InlineData("a { color: red; m10|", "margin: 10px;")]
    [InlineData("a {m10|", "margin: 10px;")]
    [InlineData("@m|", "@media ${1:screen} {\n\t${0}\n}")]
    [InlineData("a { color: #f|", "#fff")]
    [InlineData("a { color: red !|", "!important")]
    [InlineData("<style>\n  a { m10|", "margin: 10px;")]
    public void Expande_dentro_das_chaves(string marked, string expected)
    {
        var file = marked.StartsWith("<style>") ? "a.html" : "a.css";
        Assert.Equal(expected, At(marked, file));
    }

    [Theory]
    [InlineData("m10|")]
    [InlineData("a.item m10|")]
    [InlineData("a { color: m10|")]
    [InlineData("a { /* m10|")]
    [InlineData("a { content: \"m10|")]
    [InlineData("a { #f|")]
    [InlineData("a { !|")]
    [InlineData("a { $x|")]
    public void Nao_expande_fora_do_lugar(string marked) => Assert.Null(At(marked));

    // Dentro de style="...", como no VS Code: CSS de dentro de uma regra.

    [Theory]
    [InlineData("<div style=\"m10|\">", "margin: 10px;")]
    [InlineData("<div style=\"m10|", "margin: 10px;")]
    [InlineData("<div class=\"a\" style='color: red; p5|'>", "padding: 5px;")]
    [InlineData("<div style=\"m10+p5|\">", "margin: 10px; padding: 5px;")]
    [InlineData("<div style=\"color: #f|\">", "#fff")]
    [InlineData("<div style=\"color: red !|\">", "!important")]
    [InlineData("<div title=\"<?= $t ?>\" style=\"df|\">", "display: flex;")]
    [InlineData("<div style=\"color: <?= $c ?>; m10|\">", "margin: 10px;")]
    [InlineData("<p>oi</p>\n<img\n  src=\"a.png\"\n  style=\"w100p|\">", "width: 100%;")]
    public void Expande_no_atributo_style(string marked, string expected) => Assert.Equal(expected, At(marked, "a.php"));

    [Theory]
    [InlineData("<div class=\"m10|\">")]
    [InlineData("<div style=\"m10\" class=\"p5|\">")]
    [InlineData("<div style=\"@m|\">")]
    [InlineData("<div style=\"color: m10|\">")]
    [InlineData("<div style=\"<?= m10| ?>\">")]
    [InlineData("<p>style=\"m10|")]
    [InlineData("<!-- <div style=\"m10| -->")]
    [InlineData("<script>let s = '<div style=\"m10|';</script>")]
    public void Nao_expande_fora_do_style(string marked) => Assert.Null(At(marked, "a.php"));

    // No Sass, sem chaves e sem ";", como o Emmet faz.

    [Theory]
    [InlineData("nav\n  m10|", "margin: 10px")]
    [InlineData("nav\n  m10+p5|", "margin: 10px\npadding: 5px")]
    [InlineData("nav\n  poa|\n  top: 0", "position: absolute")]
    [InlineData("nav\n\tdf|", "display: flex")]
    [InlineData("nav\n  color: #f|", "#fff")]
    [InlineData("nav\n  color: red !|", "!important")]
    [InlineData("@m|", "@media ${1:screen}\n\t${0}")]
    [InlineData("nav\n  @i|", "@import url(${0})")]
    public void Expande_no_sass(string marked, string expected) => Assert.Equal(expected, At(marked, "a.sass"));

    [Theory]
    [InlineData("m10|")]
    [InlineData("nav\n  li|")]
    [InlineData("nav\n  ul m10|")]
    [InlineData("nav\n  // m10|")]
    [InlineData("nav\n  color: m10|")]
    [InlineData("nav\n  #f|")]
    public void Nao_expande_fora_do_lugar_no_sass(string marked) => Assert.Null(At(marked, "a.sass"));

    [Theory]
    [InlineData("a.sass", true)]
    [InlineData("a.scss", true)]
    [InlineData("a.less", true)]
    [InlineData("a.js", false)]
    [InlineData("a.txt", false)]
    public void Tipos_de_arquivo(string file, bool expands) => Assert.Equal(expands, At("a {\n  m10|", file) is not null);

    [Fact]
    public void Comentario_de_linha_do_scss()
    {
        Assert.Null(At("a {\n  // m10|", "a.scss"));
        Assert.NotNull(At("a {\n  // nota\n  m10|", "a.scss"));
    }

    [Fact]
    public void Html_fora_do_style_nao_e_css()
    {
        Assert.Null(At("<p>m10|", "a.html"));
        Assert.Null(At("<style>a{}</style>\n<p>m10|", "a.html"));
    }

    [Fact]
    public void Pontuacao_igual_a_do_emmet()
    {
        Assert.Equal(1, CssEmmet.Score("bd", "bd", false));
        Assert.Equal(0, CssEmmet.Score("x", "bd", false));
        Assert.True(CssEmmet.Score("ib", "inline-block", false) > CssEmmet.Score("ib", "inline-table", false));
    }
}
