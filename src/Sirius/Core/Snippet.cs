using System.Text;

namespace Sirius.Core;

// Um campo do trecho já escrito: Index 0 é onde o cursor termina. O mesmo Index em vários lugares
// é o mesmo campo, e o que se digita num aparece nos outros.
public sealed record SnippetStop(int Index, int Offset, int Length);

public sealed record SnippetText(string Text, IReadOnlyList<SnippetStop> Stops)
{
    public static SnippetText Plain(string text) => new(text, []);

    // A ordem do Tab: 1, 2, 3... e o 0 por último.
    public IReadOnlyList<int> Order => Stops.Select(stop => stop.Index).Distinct()
        .OrderBy(index => index == 0 ? int.MaxValue : index).ToList();
}

// Trechos no formato do VS Code, o mesmo dos servidores de linguagem: $1, ${1:nome}, ${1|a,b|}, $0,
// variáveis como $TM_FILENAME_BASE e \$ para um cifrão de verdade. Um leitor só para o Emmet,
// os atalhos de cada linguagem e as sugestões dos servidores.
public static class Snippet
{
    private abstract record Part;
    private sealed record Literal(string Text) : Part;
    private sealed record Field(int Index, List<Part>? Placeholder) : Part;
    private sealed record Choice(int Index, string First) : Part;
    private sealed record Variable(string Name, List<Part>? Default) : Part;

    // indent e newline adaptam o corpo ao arquivo: o Tab do trecho vira o recuo escolhido, e cada linha
    // nova ganha o recuo da linha em que o trecho entrou.
    public static SnippetText Parse(string body, Func<string, string?>? variables = null, string? indent = null, string newline = "\n", string leading = "")
    {
        var adapted = body.Replace("\r\n", "\n");
        if (indent is not null)
            adapted = adapted.Replace("\t", indent);
        adapted = adapted.Replace("\n", newline + leading);

        var position = 0;
        var parts = ParseParts(adapted, ref position, stopAt: null);

        // O texto de um campo repetido vem do primeiro que tem texto, em qualquer ordem.
        var defaults = new Dictionary<int, List<Part>>();
        CollectDefaults(parts, defaults);

        var text = new StringBuilder();
        var stops = new List<SnippetStop>();
        Render(parts, text, stops, defaults, variables ?? (_ => null), depth: 0);
        return new SnippetText(text.ToString(), stops);
    }

    private static List<Part> ParseParts(string s, ref int i, char? stopAt)
    {
        var parts = new List<Part>();
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length > 0)
                parts.Add(new Literal(literal.ToString()));
            literal.Clear();
        }

        while (i < s.Length)
        {
            var c = s[i];
            if (stopAt is { } stop && c == stop)
                break;
            if (c == '\\' && i + 1 < s.Length && s[i + 1] is '$' or '}' or '\\' or ',' or '|')
            {
                literal.Append(s[i + 1]);
                i += 2;
                continue;
            }
            if (c == '$' && TryParseDollar(s, ref i, out var part))
            {
                Flush();
                parts.Add(part);
                continue;
            }
            literal.Append(c);
            i++;
        }
        Flush();
        return parts;
    }

    private static bool TryParseDollar(string s, ref int i, out Part part)
    {
        part = null!;
        var start = i;
        if (i + 1 >= s.Length)
            return false;

        // $1 ou $NOME
        if (char.IsAsciiDigit(s[i + 1]))
        {
            var end = i + 1;
            while (end < s.Length && char.IsAsciiDigit(s[end]))
                end++;
            part = new Field(int.Parse(s[(i + 1)..end]), null);
            i = end;
            return true;
        }
        if (IsNameStart(s[i + 1]))
        {
            var end = i + 1;
            while (end < s.Length && IsNamePart(s[end]))
                end++;
            part = new Variable(s[(i + 1)..end], null);
            i = end;
            return true;
        }
        if (s[i + 1] != '{' || i + 2 >= s.Length)
            return false;

        var j = i + 2;
        if (char.IsAsciiDigit(s[j]))
        {
            var end = j;
            while (end < s.Length && char.IsAsciiDigit(s[end]))
                end++;
            var index = int.Parse(s[j..end]);
            if (end < s.Length && s[end] == '}')
            {
                part = new Field(index, null);
                i = end + 1;
                return true;
            }
            if (end < s.Length && s[end] == ':')
            {
                var k = end + 1;
                var inner = ParseParts(s, ref k, stopAt: '}');
                if (k >= s.Length)
                {
                    i = start;
                    return false;
                }
                part = new Field(index, inner);
                i = k + 1;
                return true;
            }
            if (end < s.Length && s[end] == '|')
            {
                var close = s.IndexOf("|}", end + 1, StringComparison.Ordinal);
                if (close < 0)
                    return false;
                var options = s[(end + 1)..close];
                var comma = IndexOfUnescaped(options, ',');
                part = new Choice(index, Unescape(comma < 0 ? options : options[..comma]));
                i = close + 2;
                return true;
            }
            return false;
        }

        if (IsNameStart(s[j]))
        {
            var end = j;
            while (end < s.Length && IsNamePart(s[end]))
                end++;
            var name = s[j..end];
            if (end < s.Length && s[end] == '}')
            {
                part = new Variable(name, null);
                i = end + 1;
                return true;
            }
            if (end < s.Length && s[end] == ':')
            {
                var k = end + 1;
                var inner = ParseParts(s, ref k, stopAt: '}');
                if (k >= s.Length)
                    return false;
                part = new Variable(name, inner);
                i = k + 1;
                return true;
            }
            // ${NOME/regex/formato/opções}: a transformação fica de fora, vale o valor da variável.
            if (end < s.Length && s[end] == '/')
            {
                var close = ClosingBrace(s, end);
                if (close < 0)
                    return false;
                part = new Variable(name, null);
                i = close + 1;
                return true;
            }
        }
        return false;
    }

    private static int ClosingBrace(string s, int from)
    {
        for (var k = from; k < s.Length; k++)
        {
            if (s[k] == '\\')
                k++;
            else if (s[k] == '}')
                return k;
        }
        return -1;
    }

    private static int IndexOfUnescaped(string s, char c)
    {
        for (var k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\')
                k++;
            else if (s[k] == c)
                return k;
        }
        return -1;
    }

    private static string Unescape(string s)
    {
        var result = new StringBuilder();
        for (var k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\' && k + 1 < s.Length)
                k++;
            result.Append(s[k]);
        }
        return result.ToString();
    }

    private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsNamePart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static void CollectDefaults(List<Part> parts, Dictionary<int, List<Part>> defaults)
    {
        foreach (var part in parts)
        {
            switch (part)
            {
                case Field { Placeholder: { Count: > 0 } placeholder } field:
                    defaults.TryAdd(field.Index, placeholder);
                    CollectDefaults(placeholder, defaults);
                    break;
                case Choice choice:
                    defaults.TryAdd(choice.Index, [new Literal(choice.First)]);
                    break;
                case Variable { Default: { } inner }:
                    CollectDefaults(inner, defaults);
                    break;
            }
        }
    }

    private const int MaxDepth = 20;

    // As variáveis do VS Code: sem valor, somem do texto, em vez de virar "$TM_SELECTED_TEXT".
    private static readonly HashSet<string> KnownVariables = new(StringComparer.Ordinal)
    {
        "TM_SELECTED_TEXT", "TM_CURRENT_LINE", "TM_CURRENT_WORD", "TM_LINE_INDEX", "TM_LINE_NUMBER", "TM_FILENAME", "TM_FILENAME_BASE",
        "TM_DIRECTORY", "TM_FILEPATH", "CLIPBOARD", "WORKSPACE_NAME", "CURRENT_YEAR", "CURRENT_MONTH", "CURRENT_DATE", "UUID",
        "BLOCK_COMMENT_START", "BLOCK_COMMENT_END", "LINE_COMMENT",
    };

    private static void Render(List<Part> parts, StringBuilder text, List<SnippetStop> stops, Dictionary<int, List<Part>> defaults,
        Func<string, string?> variables, int depth)
    {
        if (depth > MaxDepth)
            return;
        foreach (var part in parts)
        {
            switch (part)
            {
                case Literal literal:
                    text.Append(literal.Text);
                    break;
                case Field field:
                {
                    var start = text.Length;
                    // Um campo repetido sem texto próprio mostra o texto do outro, como o $i de um for.
                    var content = field.Placeholder ?? (field.Index != 0 && defaults.TryGetValue(field.Index, out var shared) ? shared : null);
                    if (content is not null)
                        Render(content, text, stops, defaults, variables, depth + 1);
                    stops.Add(new SnippetStop(field.Index, start, text.Length - start));
                    break;
                }
                case Choice choice:
                    stops.Add(new SnippetStop(choice.Index, text.Length, choice.First.Length));
                    text.Append(choice.First);
                    break;
                case Variable variable:
                    if (variables(variable.Name) is { } value)
                        text.Append(value);
                    else if (variable.Default is not null)
                        Render(variable.Default, text, stops, defaults, variables, depth + 1);
                    // Desconhecida e sem texto padrão: no PHP, um $nome que o servidor esqueceu de escapar é a variável dele.
                    else if (!KnownVariables.Contains(variable.Name))
                        text.Append('$').Append(variable.Name);
                    break;
            }
        }
    }
}
