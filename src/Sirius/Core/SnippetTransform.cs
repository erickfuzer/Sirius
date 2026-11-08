using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// A transformação dos trechos do VS Code: ${NOME/regex/formato/opções}. O formato aceita $1, ${1},
// ${1:/upcase}, ${1:/downcase}, ${1:/capitalize}, ${1:/camelcase}, ${1:/pascalcase}, ${1:+se},
// ${1:?se:senão}, ${1:-senão} e ${1:senão}, com o mesmo resultado do VS Code.
public sealed class SnippetTransform
{
    private abstract record Piece;
    private sealed record Text(string Value) : Piece;
    private sealed record Group(int Index, string? Shorthand, string? If, string? Else) : Piece;

    private readonly Regex _pattern;
    private readonly bool _global;
    private readonly List<Piece> _format;

    private SnippetTransform(Regex pattern, bool global, List<Piece> format)
    {
        _pattern = pattern;
        _global = global;
        _format = format;
    }

    // slash é a posição da primeira "/", logo depois do nome. close fica na "}" que fecha a variável.
    public static bool TryParse(string s, int slash, out SnippetTransform transform, out int close)
    {
        transform = null!;
        close = -1;
        var k = slash + 1;

        var regex = new StringBuilder();
        for (; k < s.Length && s[k] != '/'; k++)
        {
            // \/ é a barra dentro da regex; as outras barras invertidas são da própria regex.
            if (s[k] == '\\' && k + 1 < s.Length)
            {
                if (s[k + 1] != '/')
                    regex.Append('\\');
                regex.Append(s[++k]);
            }
            else
                regex.Append(s[k]);
        }
        if (k >= s.Length)
            return false;

        k++;
        var format = new List<Piece>();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0)
                format.Add(new Text(text.ToString()));
            text.Clear();
        }
        while (k < s.Length && s[k] != '/')
        {
            if (s[k] == '\\' && k + 1 < s.Length)
            {
                text.Append(s[k + 1]);
                k += 2;
            }
            else if (s[k] == '$' && TryParseGroup(s, ref k, out var group))
            {
                Flush();
                format.Add(group);
            }
            else
                text.Append(s[k++]);
        }
        if (k >= s.Length)
            return false;
        Flush();

        k++;
        var optionsStart = k;
        while (k < s.Length && s[k] != '}')
            k++;
        if (k >= s.Length)
            return false;
        var options = s[optionsStart..k];

        var regexOptions = RegexOptions.CultureInvariant;
        if (options.Contains('i'))
            regexOptions |= RegexOptions.IgnoreCase;
        if (options.Contains('m'))
            regexOptions |= RegexOptions.Multiline;
        if (options.Contains('s'))
            regexOptions |= RegexOptions.Singleline;
        try
        {
            // O trecho pode vir de um servidor de linguagem: uma regex lenta não pode travar a digitação.
            var pattern = new Regex(regex.ToString(), regexOptions, TimeSpan.FromMilliseconds(200));
            transform = new SnippetTransform(pattern, options.Contains('g'), format);
        }
        catch (ArgumentException)
        {
            return false;
        }
        close = k;
        return true;
    }

    private static bool TryParseGroup(string s, ref int k, out Piece group)
    {
        group = null!;
        var start = k;
        // $1
        if (k + 1 < s.Length && char.IsAsciiDigit(s[k + 1]))
        {
            var end = k + 1;
            while (end < s.Length && char.IsAsciiDigit(s[end]))
                end++;
            group = new Group(int.Parse(s[(k + 1)..end], CultureInfo.InvariantCulture), null, null, null);
            k = end;
            return true;
        }
        if (k + 2 >= s.Length || s[k + 1] != '{' || !char.IsAsciiDigit(s[k + 2]))
            return false;

        var j = k + 2;
        while (j < s.Length && char.IsAsciiDigit(s[j]))
            j++;
        var index = int.Parse(s[(k + 2)..j], CultureInfo.InvariantCulture);
        if (j < s.Length && s[j] == '}')
        {
            group = new Group(index, null, null, null);
            k = j + 1;
            return true;
        }
        if (j >= s.Length || s[j] != ':')
            return false;

        j++;
        if (j < s.Length && s[j] == '/')
        {
            var end = s.IndexOf('}', j);
            if (end < 0)
                return false;
            group = new Group(index, s[(j + 1)..end], null, null);
            k = end + 1;
            return true;
        }

        var kind = j < s.Length ? s[j] : '\0';
        if (kind is '+' or '?' or '-')
            j++;
        var first = ReadUntil(s, ref j, kind == '?' ? ":}" : "}");
        string? second = null;
        if (kind == '?' && j < s.Length && s[j] == ':')
        {
            j++;
            second = ReadUntil(s, ref j, "}");
        }
        if (j >= s.Length || s[j] != '}')
        {
            k = start;
            return false;
        }
        group = kind switch
        {
            '+' => new Group(index, null, first, null),
            '?' => new Group(index, null, first, second ?? ""),
            _ => new Group(index, null, null, first),
        };
        k = j + 1;
        return true;
    }

    private static string ReadUntil(string s, ref int j, string stops)
    {
        var text = new StringBuilder();
        while (j < s.Length && !stops.Contains(s[j]))
        {
            if (s[j] == '\\' && j + 1 < s.Length)
                j++;
            text.Append(s[j++]);
        }
        return text.ToString();
    }

    public string Apply(string value)
    {
        var matched = false;
        string result;
        try
        {
            MatchEvaluator evaluate = match =>
            {
                matched = true;
                return Replace(match.Groups);
            };
            result = _global ? _pattern.Replace(value, evaluate) : _pattern.Replace(value, evaluate, 1);
        }
        catch (RegexMatchTimeoutException)
        {
            return value;
        }
        // Sem nenhuma correspondência, o VS Code ainda usa os "senão" do formato.
        if (!matched && _format.Any(piece => piece is Group { Else: not null }))
            result = Replace(null);
        return result;
    }

    private string Replace(GroupCollection? groups)
    {
        var result = new StringBuilder();
        foreach (var piece in _format)
        {
            if (piece is Text text)
            {
                result.Append(text.Value);
                continue;
            }
            var group = (Group)piece;
            var value = groups is not null && group.Index < groups.Count && groups[group.Index].Success ? groups[group.Index].Value : "";
            result.Append(Resolve(group, value));
        }
        return result.ToString();
    }

    private static string Resolve(Group group, string value) => group.Shorthand switch
    {
        "upcase" => value.ToUpperInvariant(),
        "downcase" => value.ToLowerInvariant(),
        "capitalize" => value.Length == 0 ? "" : char.ToUpperInvariant(value[0]) + value[1..],
        "pascalcase" => Words(value, camel: false),
        "camelcase" => Words(value, camel: true),
        _ when value.Length > 0 && group.If is not null => group.If,
        _ when value.Length == 0 && group.Else is not null => group.Else,
        _ => value,
    };

    private static string Words(string value, bool camel)
    {
        var words = Regex.Matches(value, "[a-z0-9]+", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
        if (words.Count == 0)
            return value;
        return string.Concat(words.Select((word, i) =>
            (camel && i == 0 ? char.ToLowerInvariant(word[0]) : char.ToUpperInvariant(word[0])) + word[1..]));
    }
}
