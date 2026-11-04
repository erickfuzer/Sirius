using System.Text;
using AvaloniaEdit.Document;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("sirius-test-").FullName;

    public string File(string relative, string? content = null)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        if (content is not null)
            System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    public void Dispose() => Directory.Delete(Path, true);
}

public class TextFileTests
{
    private static byte[] RoundTrip(TempFolder temp, byte[] original)
    {
        var path = temp.File("arquivo.txt");
        File.WriteAllBytes(path, original);
        var loaded = TextFile.Load(path);
        TextFile.Save(path, loaded.Text, loaded.Encoding);
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void Utf8_sem_bom_continua_igual()
    {
        using var temp = new TempFolder();
        var original = new UTF8Encoding(false).GetBytes("olá, coração\n");
        Assert.Equal(original, RoundTrip(temp, original));
    }

    [Fact]
    public void Utf8_com_bom_mantem_o_bom()
    {
        using var temp = new TempFolder();
        byte[] original = [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes("olá")];
        Assert.Equal(original, RoundTrip(temp, original));
    }

    [Fact]
    public void Utf16_continua_igual()
    {
        using var temp = new TempFolder();
        byte[] original = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("olá")];
        Assert.Equal(original, RoundTrip(temp, original));
    }

    [Fact]
    public void Latin1_nao_estraga_acentos()
    {
        using var temp = new TempFolder();
        var original = Encoding.Latin1.GetBytes("coração");
        Assert.Equal(original, RoundTrip(temp, original));
    }

    [Fact]
    public void Quebras_de_linha_do_windows_sao_mantidas()
    {
        using var temp = new TempFolder();
        var original = Encoding.UTF8.GetBytes("a\r\nb\nc\r\n");
        Assert.Equal(original, RoundTrip(temp, original));
    }

    [Fact]
    public void Arquivo_binario_e_recusado()
    {
        using var temp = new TempFolder();
        var path = temp.File("foto.bin");
        File.WriteAllBytes(path, [1, 2, 0, 3]);
        Assert.Throws<TextFileException>(() => TextFile.Load(path));
    }

    [Fact]
    public void Salvar_nao_deixa_arquivo_temporario()
    {
        using var temp = new TempFolder();
        var path = temp.File("novo.txt");
        TextFile.Save(path, "oi", new UTF8Encoding(false));
        Assert.Equal(["novo.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));
    }
}

public class SyntaxHighlighterTests
{
    private static (TextDocument Document, SyntaxHighlighter Highlighter) Open(string file, string text)
    {
        var document = new TextDocument(text);
        return (document, SyntaxHighlighter.ForDocument(file, document)!);
    }

    private static List<(string Text, TokenKind Kind)> Tokens(TextDocument document, SyntaxHighlighter highlighter, int lineNumber)
    {
        var line = document.GetText(document.GetLineByNumber(lineNumber));
        return highlighter.TokensFor(lineNumber).Select(t => (line.Substring(t.Start, t.Length), t.Kind)).ToList();
    }

    private static bool AllComment(TextDocument document, SyntaxHighlighter highlighter, int lineNumber) =>
        Tokens(document, highlighter, lineNumber).All(t => t.Kind == TokenKind.Comment);

    [Fact]
    public void Reconhece_palavra_chave_e_nome()
    {
        var (document, highlighter) = Open("a.cs", "public class Buffer { }");
        var tokens = Tokens(document, highlighter, 1);
        Assert.Contains(("public", TokenKind.Keyword), tokens);
        Assert.Contains(("class", TokenKind.Keyword), tokens);
        Assert.Contains(("Buffer", TokenKind.Type), tokens);
    }

    [Fact]
    public void New_do_csharp_tem_o_peso_de_palavra_chave()
    {
        var (document, highlighter) = Open("a.cs", "class A { object b = new object(); }");
        Assert.Contains(("new", TokenKind.Keyword), Tokens(document, highlighter, 1));
    }

    [Fact]
    public void Barras_dentro_de_texto_nao_viram_comentario()
    {
        var (document, highlighter) = Open("a.cs", "var u = \"http://site\";");
        var tokens = Tokens(document, highlighter, 1);
        Assert.Contains(tokens, t => t.Kind == TokenKind.String && t.Text.Contains("http"));
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Comment);
    }

    [Fact]
    public void Comentario_de_varias_linhas_vale_para_todas()
    {
        var (document, highlighter) = Open("a.cs", "/* um\ndois\ntrês */\nclass A { }");
        Assert.True(AllComment(document, highlighter, 1));
        Assert.True(AllComment(document, highlighter, 2));
        Assert.True(AllComment(document, highlighter, 3));
        Assert.Contains(("class", TokenKind.Keyword), Tokens(document, highlighter, 4));
    }

    [Fact]
    public void Abrir_comentario_muda_as_linhas_de_baixo()
    {
        var (document, highlighter) = Open("a.cs", "int a;\nclass A { }");
        Assert.Contains(("class", TokenKind.Keyword), Tokens(document, highlighter, 2));

        document.Insert(0, "/* ");

        Assert.True(AllComment(document, highlighter, 2));

        document.Remove(0, 3);

        Assert.Contains(("class", TokenKind.Keyword), Tokens(document, highlighter, 2));
    }

    [Fact]
    public void Apagar_linhas_nao_quebra_a_analise()
    {
        var (document, highlighter) = Open("a.cs", "int a;\nint b;\nint c;");
        highlighter.TokensFor(3);

        document.Remove(0, document.GetLineByNumber(3).Offset);

        Assert.Equal(1, document.LineCount);
        Assert.Contains(("int", TokenKind.Keyword), Tokens(document, highlighter, 1));
    }

    [Fact]
    public void Acentos_nao_desalinham_as_posicoes()
    {
        var (document, highlighter) = Open("a.py", "coração = 1 # ação");
        var tokens = Tokens(document, highlighter, 1);
        Assert.Contains(tokens, t => t.Kind == TokenKind.Comment && t.Text.Contains("ação"));
        Assert.Contains(("1", TokenKind.Number), tokens);
    }

    [Theory]
    [InlineData("a.ts")]
    [InlineData("a.py")]
    [InlineData("a.rs")]
    [InlineData("a.go")]
    [InlineData("a.json")]
    [InlineData("a.html")]
    [InlineData("a.css")]
    [InlineData("a.md")]
    [InlineData("a.axaml")]
    [InlineData("a.php")]
    public void Linguagens_comuns_tem_gramatica(string file)
    {
        Assert.NotNull(SyntaxHighlighter.ForDocument(file, new TextDocument("x")));
    }

    [Theory]
    [InlineData("notas.xyz")]
    [InlineData("LICENSE")]
    public void Arquivo_desconhecido_fica_sem_destaque(string file)
    {
        Assert.Null(SyntaxHighlighter.ForDocument(file, new TextDocument("x")));
    }
}

public class SyntaxScopesTests
{
    [Fact]
    public void Barras_do_comentario_contam_como_comentario()
    {
        Assert.Equal(TokenKind.Comment, SyntaxScopes.Classify(["source.cs", "comment.line.double-slash", "punctuation.definition.comment.cs"]));
    }

    [Fact]
    public void Interpolacao_dentro_de_texto_nao_e_texto()
    {
        Assert.Null(SyntaxScopes.Classify(["source.cs", "string.quoted.double", "meta.interpolation", "variable.other"]));
    }

    [Fact]
    public void Operador_e_pontuacao_e_nao_palavra_chave()
    {
        Assert.Equal(TokenKind.Punctuation, SyntaxScopes.Classify(["source.cs", "keyword.operator.assignment"]));
    }

    [Fact]
    public void Operador_escrito_como_palavra_e_palavra_chave()
    {
        Assert.Equal(TokenKind.Keyword, SyntaxScopes.Classify(["source.cs", "keyword.operator.expression.new"], 'n'));
    }

    [Fact]
    public void Variavel_fica_no_peso_normal()
    {
        Assert.Null(SyntaxScopes.Classify(["source.cs", "entity.name.variable.parameter"]));
    }

    [Theory]
    [InlineData("entity.name.function.cs", TokenKind.Function)]
    [InlineData("support.function.python", TokenKind.Function)]
    [InlineData("entity.name.type.class.cs", TokenKind.Type)]
    [InlineData("entity.name.tag.html", TokenKind.Type)]
    [InlineData("support.class.js", TokenKind.Type)]
    [InlineData("constant.numeric.decimal", TokenKind.Number)]
    [InlineData("string.quoted.double", TokenKind.String)]
    [InlineData("constant.character.escape", TokenKind.String)]
    public void Cada_tipo_de_codigo_recebe_sua_cor(string scope, TokenKind expected)
    {
        Assert.Equal(expected, SyntaxScopes.Classify(["source", scope]));
    }

    [Fact]
    public void Prefixo_precisa_ser_um_escopo_inteiro()
    {
        Assert.Null(SyntaxScopes.Classify(["source.cs", "commentary"]));
    }
}

public class FileOperationsTests
{
    [Theory]
    [InlineData("a.cs")]
    [InlineData(".gitignore")]
    [InlineData("coração.txt")]
    [InlineData("sem extensao")]
    public void Nomes_validos(string name)
    {
        Assert.Null(FileOperations.ValidateName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b.cs")]
    [InlineData("a\\b.cs")]
    [InlineData("..")]
    [InlineData("..\\fora.txt")]
    [InlineData("a?.cs")]
    [InlineData("termina.")]
    [InlineData("termina ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    public void Nomes_invalidos(string name)
    {
        Assert.NotNull(FileOperations.ValidateName(name));
    }

    [Fact]
    public void Criar_arquivo_nunca_sobrescreve()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.txt", "importante");

        Assert.Throws<IOException>(() => FileOperations.CreateFile(temp.Path, "a.txt"));

        Assert.Equal("importante", File.ReadAllText(path));
    }

    [Fact]
    public void Criar_pasta_com_nome_de_arquivo_existente_falha()
    {
        using var temp = new TempFolder();
        temp.File("a", "x");
        Assert.Throws<IOException>(() => FileOperations.CreateFolder(temp.Path, "a"));
    }

    [Fact]
    public void Renomear_arquivo_mantem_o_conteudo()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.txt", "conteudo");

        var target = FileOperations.Rename(path, "b.txt");

        Assert.False(File.Exists(path));
        Assert.Equal("conteudo", File.ReadAllText(target));
    }

    [Fact]
    public void Renomear_nunca_sobrescreve()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.txt", "a");
        var other = temp.File("b.txt", "importante");

        Assert.Throws<IOException>(() => FileOperations.Rename(path, "b.txt"));

        Assert.Equal("importante", File.ReadAllText(other));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Renomear_so_as_maiusculas_funciona_em_arquivo_e_pasta()
    {
        using var temp = new TempFolder();
        var file = temp.File("nota.txt", "x");
        temp.File(Path.Combine("pasta", "dentro.txt"), "y");

        FileOperations.Rename(file, "NOTA.txt");
        FileOperations.Rename(Path.Combine(temp.Path, "pasta"), "PASTA");

        var names = new DirectoryInfo(temp.Path).EnumerateFileSystemInfos().Select(i => i.Name).ToList();
        Assert.Contains("NOTA.txt", names);
        Assert.Contains("PASTA", names);
        Assert.True(File.Exists(Path.Combine(temp.Path, "PASTA", "dentro.txt")));
    }

    [Theory]
    [InlineData(@"C:\p\a.cs", @"C:\p\a.cs", true)]
    [InlineData(@"C:\p\src\a.cs", @"C:\p\src", true)]
    [InlineData(@"C:\P\SRC\a.cs", @"c:\p\src", true)]
    [InlineData(@"C:\p\src2\a.cs", @"C:\p\src", false)]
    public void Reconhece_o_que_esta_dentro_de_uma_pasta(string path, string folder, bool expected)
    {
        Assert.Equal(expected, FileOperations.IsInside(path, folder));
    }
}

public class ProjectSearchTests
{
    private static readonly IReadOnlyDictionary<string, string> NoUnsaved = new Dictionary<string, string>();

    private static SearchResult Search(TempFolder temp, SearchQuery query, IReadOnlyDictionary<string, string>? unsaved = null) =>
        ProjectSearch.Run(ProjectTree.FindFiles(temp.Path), query, unsaved ?? NoUnsaved, CancellationToken.None);

    [Fact]
    public void Encontra_linha_e_coluna_ignorando_maiusculas()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "primeira\r\n  Alvo aqui\r\nterceira");

        var hit = Assert.Single(Search(temp, new SearchQuery("alvo")).Hits);

        Assert.Equal(2, hit.Line);
        Assert.Equal(2, hit.Column);
        Assert.Equal("Alvo", hit.Match);
        Assert.Equal(" aqui", hit.After);
    }

    [Fact]
    public void Diferenciar_maiusculas_filtra()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "Alvo\nalvo");
        var hit = Assert.Single(Search(temp, new SearchQuery("alvo", MatchCase: true)).Hits);
        Assert.Equal(2, hit.Line);
    }

    [Fact]
    public void Expressao_regular_funciona()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "x = 123\ny = abc");
        var hit = Assert.Single(Search(temp, new SearchQuery(@"\d+", UseRegex: true)).Hits);
        Assert.Equal("123", hit.Match);
    }

    [Fact]
    public void Expressao_invalida_e_avisada_sem_quebrar()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "x");
        Assert.True(Search(temp, new SearchQuery("(", UseRegex: true)).InvalidQuery);
    }

    [Fact]
    public void Expressao_que_casa_vazio_nao_trava()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "abc");
        Assert.Empty(Search(temp, new SearchQuery("x*", UseRegex: true)).Hits);
    }

    [Fact]
    public void Arquivo_binario_e_ignorado()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("foto.bin"), [(byte)'a', (byte)'l', (byte)'v', (byte)'o', 0, 1]);
        temp.File("a.cs", "alvo");
        var hit = Assert.Single(Search(temp, new SearchQuery("alvo")).Hits);
        Assert.EndsWith("a.cs", hit.FullPath);
    }

    [Fact]
    public void Texto_nao_salvo_tem_prioridade_sobre_o_disco()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "antigo");
        var unsaved = new Dictionary<string, string> { [path] = "novo" };

        Assert.Empty(Search(temp, new SearchQuery("antigo"), unsaved).Hits);
        Assert.Single(Search(temp, new SearchQuery("novo"), unsaved).Hits);
    }

    [Fact]
    public void Muitos_resultados_param_no_limite_e_avisam()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", string.Join("\n", Enumerable.Repeat("alvo", ProjectSearch.MaxHits + 5)));
        var result = Search(temp, new SearchQuery("alvo"));
        Assert.Equal(ProjectSearch.MaxHits, result.Hits.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Linha_comprida_mostra_so_um_trecho()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", new string('x', 5000) + "alvo" + new string('y', 5000));
        var hit = Assert.Single(Search(temp, new SearchQuery("alvo")).Hits);
        Assert.StartsWith("…", hit.Before);
        Assert.EndsWith("…", hit.After);
        Assert.True(hit.Before.Length + hit.After.Length < 200);
        Assert.Equal(5000, hit.Column);
    }

    [Fact]
    public void Busca_cancelada_para()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "alvo");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ProjectSearch.Run(ProjectTree.FindFiles(temp.Path), new SearchQuery("alvo"), NoUnsaved, cancel.Token));
    }
}

public class ProjectTreeTests
{
    [Fact]
    public void Pastas_vem_primeiro_e_bin_fica_de_fora()
    {
        using var temp = new TempFolder();
        temp.File("b.txt", "");
        temp.File(Path.Combine("src", "a.cs"), "");
        temp.File(Path.Combine("bin", "lixo.dll"), "");

        var names = ProjectTree.List(temp.Path).Select(e => e.Name);

        Assert.Equal(["src", "b.txt"], names);
    }

    [Fact]
    public void Busca_encontra_arquivos_em_subpastas_e_respeita_o_limite()
    {
        using var temp = new TempFolder();
        temp.File(Path.Combine("a", "b", "c.cs"), "");
        temp.File("d.cs", "");
        temp.File(Path.Combine("node_modules", "x.js"), "");

        Assert.Equal(2, ProjectTree.FindFiles(temp.Path).Count);
        Assert.Single(ProjectTree.FindFiles(temp.Path, limit: 1));
    }

    [Fact]
    public void Le_o_branch_atual_do_git()
    {
        using var temp = new TempFolder();
        temp.File(Path.Combine(".git", "HEAD"), "ref: refs/heads/main\n");
        Assert.Equal("main", ProjectTree.CurrentBranch(temp.Path));
    }

    [Fact]
    public void Pasta_sem_git_nao_tem_branch()
    {
        using var temp = new TempFolder();
        Assert.Null(ProjectTree.CurrentBranch(temp.Path));
    }
}
