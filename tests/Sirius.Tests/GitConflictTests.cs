using Sirius.Core;

namespace Sirius.Tests;

// As marcas de conflito do Git, lidas e resolvidas sem a janela.
public class GitConflictTests
{
    private const string Simple = "antes\n<<<<<<< HEAD\nOlá\nteste\n=======\n# test\n>>>>>>> 18de7c3\ndepois\n";

    [Fact]
    public void Separa_os_dois_lados()
    {
        var sides = GitConflict.Sides(Simple)!.Value;
        Assert.Equal("antes\nOlá\nteste\ndepois\n", sides.Mine);
        Assert.Equal("antes\n# test\ndepois\n", sides.Received);
        Assert.Equal(1, sides.Count);
    }

    [Theory]
    [InlineData(ConflictChoice.Mine, "antes\nOlá\nteste\ndepois\n")]
    [InlineData(ConflictChoice.Received, "antes\n# test\ndepois\n")]
    [InlineData(ConflictChoice.Both, "antes\nOlá\nteste\n# test\ndepois\n")]
    public void Resolve_com_o_lado_escolhido(ConflictChoice choice, string expected) =>
        Assert.Equal(expected, GitConflict.Resolve(Simple, choice));

    [Fact]
    public void Varios_conflitos_e_quebra_de_linha_do_windows()
    {
        var text = "<<<<<<< HEAD\r\na\r\n=======\r\nb\r\n>>>>>>> x\r\nmeio\r\n<<<<<<< HEAD\r\nc\r\n=======\r\nd\r\n>>>>>>> x\r\n";
        Assert.Equal(2, GitConflict.Sides(text)!.Value.Count);
        Assert.Equal("b\r\nmeio\r\nd\r\n", GitConflict.Resolve(text, ConflictChoice.Received));
    }

    [Fact]
    public void Base_do_diff3_fica_de_fora()
    {
        var text = "<<<<<<< HEAD\nmeu\n||||||| base\nantigo\n=======\nrecebido\n>>>>>>> x\n";
        Assert.Equal("meu\nrecebido\n", GitConflict.Resolve(text, ConflictChoice.Both));
    }

    [Fact]
    public void Lado_vazio_vale()
    {
        var text = "<<<<<<< HEAD\n=======\nnovo\n>>>>>>> x\n";
        Assert.Equal("", GitConflict.Resolve(text, ConflictChoice.Mine));
        Assert.Equal("novo\n", GitConflict.Resolve(text, ConflictChoice.Received));
    }

    [Theory]
    [InlineData("sem conflito\n")]
    [InlineData("<<<<<<< HEAD\na\n=======\nb\n")]
    [InlineData("a\n=======\nb\n")]
    [InlineData("<<<<<<< HEAD\na\n<<<<<<< HEAD\n=======\nb\n>>>>>>> x\n")]
    [InlineData("")]
    public void Marca_solta_nao_e_resolvida(string text)
    {
        Assert.Null(GitConflict.Sides(text));
        Assert.Null(GitConflict.Resolve(text, ConflictChoice.Mine));
    }

    [Fact]
    public void Texto_parecido_com_marca_nao_conta()
    {
        // Um título de Markdown sublinhado com = e uma seta no texto não são marcas.
        var text = "Título\n========\n<<<<<<<< oito\n";
        Assert.Null(GitConflict.Sides(text));
    }
}
