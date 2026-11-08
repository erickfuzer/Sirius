using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// Uma abreviação de CSS no cursor, como m10, e o trecho que ela vira, no formato do VS Code (${1:texto}).
public sealed record CssExpansion(int Start, int Length, string Abbreviation, string Snippet);

// O Emmet de CSS, como no VS Code: m10 vira margin: 10px;, poa vira position: absolute; e d:f vira
// display: flex;. Cópia do jeito do próprio Emmet (busca aproximada pela pontuação dele, palavras-chave e
// unidades), com a tabela oficial em Assets\Snippets\emmet-css.json, licença MIT.
public static class CssEmmet
{
    private const double MinScore = 0.3;
    private const int MaxAbbreviationLength = 100;

    private static readonly string[] GlobalKeywords = ["auto", "inherit", "unset", "none"];
    private static readonly HashSet<string> Unitless = ["z-index", "line-height", "opacity", "font-weight", "zoom", "flex", "flex-grow", "flex-shrink"];
    private static readonly Dictionary<string, string> UnitAliases = new() { ["p"] = "%", ["e"] = "em", ["x"] = "ex", ["r"] = "rem" };

    private static readonly Regex HexColor = new(@"^#[\da-fA-F]{0,6}$", RegexOptions.CultureInvariant);
    private static readonly Regex PropertyHexColor = new(@"^[a-zA-Z]+:?#[\d.a-fA-F]{0,6}$", RegexOptions.CultureInvariant);
    private static readonly Regex ValidStart = new(@"^-?[a-zA-Z!@#]", RegexOptions.CultureInvariant);
    private static readonly Regex PropertyPattern = new(@"^([a-z-]+)(?:\s*:\s*([^\n\r;]+?);*)?$", RegexOptions.CultureInvariant);

    public static bool Supports(string? path) =>
        IsStylesheet(path) || IsSass(path) || Emmet.Supports(path);

    private static bool IsStylesheet(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() is ".css" or ".scss" or ".less";

    // O Sass sem chaves: cada linha é uma declaração, e o recuo diz o que está dentro de quê.
    public static bool IsSass(string? path) => Path.GetExtension(path ?? "").Equals(".sass", StringComparison.OrdinalIgnoreCase);

    // No Sass, uma tag sozinha numa linha com recuo é um seletor aninhado, como "  li". Sem servidor para o
    // Sass, a lista só teria o Emmet, e o Enter para ir à linha de baixo trocaria o li por list-style.
    private static readonly HashSet<string> TagNames =
    [
        "a", "abbr", "article", "aside", "audio", "b", "blockquote", "body", "button", "canvas", "caption", "code",
        "dd", "details", "dialog", "div", "dl", "dt", "em", "fieldset", "figcaption", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "html", "i", "iframe", "img", "input", "label", "legend",
        "li", "main", "mark", "menu", "nav", "ol", "option", "p", "pre", "q", "s", "section", "select", "small",
        "span", "strong", "sub", "summary", "sup", "svg", "table", "tbody", "td", "textarea", "tfoot", "th", "thead",
        "tr", "u", "ul", "video",
    ];

    // A tabela

    private abstract record CssSnippet(string Key);
    private sealed record RawSnippet(string Key, string Value) : CssSnippet(Key);

    private sealed record PropertySnippet(string Key, string Property, List<string> Alternatives, Dictionary<string, string> Keywords) : CssSnippet(Key)
    {
        public List<PropertySnippet> Dependencies { get; } = [];
    }

    private static readonly Lazy<(List<CssSnippet> Snippets, List<string> Properties)> Table = new(LoadTable);

    private static (List<CssSnippet>, List<string>) LoadTable()
    {
        var snippets = new List<CssSnippet>();
        using (var stream = SnippetLibrary.Resource("emmet-css.json"))
        {
            if (stream is not null)
            {
                using var document = JsonDocument.Parse(stream);
                foreach (var item in document.RootElement.EnumerateObject())
                {
                    var value = item.Value.GetString() ?? "";
                    foreach (var key in item.Name.Split('|'))
                        snippets.Add(Create(key.Trim(), value));
                }
            }
        }

        // Ordenados pela chave, como no Emmet: no empate da pontuação, vence o último.
        snippets.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        Nest(snippets);
        var properties = snippets.OfType<PropertySnippet>().Select(s => s.Property).Distinct().ToList();
        return (snippets, properties);
    }

    private static CssSnippet Create(string key, string value)
    {
        var match = PropertyPattern.Match(value);
        if (!match.Success)
            return new RawSnippet(key, value);

        var alternatives = match.Groups[2].Success ? match.Groups[2].Value.Split('|').Select(a => a.Trim()).ToList() : [];
        var keywords = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alternative in alternatives)
            CollectKeywords(alternative, keywords);
        return new PropertySnippet(key, match.Groups[1].Value, alternatives, keywords);
    }

    // As palavras que valem como valor: as escritas, o nome das funções (com o texto todo) e o texto dos campos.
    private static void CollectKeywords(string alternative, Dictionary<string, string> keywords)
    {
        foreach (var token in SplitAlternative(alternative))
        {
            if (FieldText(token) is { } placeholder)
            {
                if (placeholder.Trim() is { Length: > 0 } trimmed)
                    keywords.TryAdd(trimmed, trimmed);
            }
            else if (token.IndexOf('(') is > 0 and var paren && IsKeywordText(token[..paren]))
            {
                keywords.TryAdd(token[..paren], token);
            }
            else if (IsKeywordText(token) && !char.IsAsciiDigit(token[0]))
            {
                keywords.TryAdd(token, token);
            }
        }
    }

    private static bool IsKeywordText(string text) => text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // Separa por espaço, sem cortar um campo como ${1:inset } nem os argumentos de uma função.
    private static List<string> SplitAlternative(string alternative)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var braces = 0;
        var parens = 0;
        for (var i = 0; i < alternative.Length; i++)
        {
            var c = alternative[i];
            if (c == '$' && i + 1 < alternative.Length && alternative[i + 1] == '{')
            {
                // Um campo encostado no anterior é outro valor.
                if (braces == 0 && parens == 0 && current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                braces++;
                current.Append("${");
                i++;
                continue;
            }
            if (c == '}' && braces > 0)
                braces--;
            else if (c == '(' && braces == 0)
                parens++;
            else if (c == ')' && braces == 0 && parens > 0)
                parens--;
            else if (char.IsWhiteSpace(c) && braces == 0 && parens == 0)
            {
                if (current.Length > 0)
                    tokens.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
            if (c == '}' && braces == 0 && parens == 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static string? FieldText(string token)
    {
        var match = Regex.Match(token, @"^\$\{\d+(?::(.*))?\}$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    // background-position-x fica dentro de background-position, que fica dentro de background: as palavras
    // de um servem para o outro.
    private static void Nest(List<CssSnippet> snippets)
    {
        var stack = new List<PropertySnippet>();
        foreach (var current in snippets.OfType<PropertySnippet>())
        {
            while (stack.Count > 0)
            {
                var previous = stack[^1];
                if (current.Property.StartsWith(previous.Property, StringComparison.Ordinal)
                    && current.Property.Length > previous.Property.Length && current.Property[previous.Property.Length] == '-')
                {
                    previous.Dependencies.Add(current);
                    stack.Add(current);
                    break;
                }
                stack.RemoveAt(stack.Count - 1);
            }
            if (stack.Count == 0)
                stack.Add(current);
        }
    }

    // Onde vale

    private enum Place { None, TopLevel, Property, Value }

    // A abreviação que termina no cursor, em .css, .scss, .less, .sass, dentro de <style> ou de style="...".
    public static CssExpansion? TryExpandAt(string text, int caret, string? path)
    {
        if (caret <= 0 || caret > text.Length)
            return null;

        int regionStart;
        var inAttribute = false;
        var sass = IsSass(path);
        if (IsStylesheet(path))
            regionStart = 0;
        else if (sass)
            regionStart = text.LastIndexOf('\n', caret - 1) + 1;
        else if (!Emmet.Supports(path) || Markup.InPhp(text, caret) || Markup.InComment(text, caret))
            return null;
        else if (Markup.InStyle(text, caret))
            regionStart = Markup.StyleContentStart(text, caret);
        // Como no VS Code, o valor do style é CSS de dentro de uma regra.
        else if (!Markup.InScript(text, caret) && Markup.StyleAttributeStart(text, caret) is >= 0 and var value)
        {
            regionStart = value;
            inAttribute = true;
        }
        else
            return null;

        var start = caret;
        while (start > regionStart && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('{' or '}' or ';'))
            start--;
        if (start == caret || caret - start > MaxAbbreviationLength)
            return null;
        var abbreviation = text[start..caret];

        // Como no VS Code: um # só como cor, e nada que não comece por letra, !, @ ou #.
        if (!ValidStart.IsMatch(abbreviation))
            return null;
        if (abbreviation.Contains('#') && !HexColor.IsMatch(abbreviation) && !PropertyHexColor.IsMatch(abbreviation))
            return null;
        // Num atributo não cabe @media nem outra regra.
        if (inAttribute && abbreviation.StartsWith('@'))
            return null;
        if (sass && TagNames.Contains(abbreviation))
            return null;

        var lineComments = sass || Path.GetExtension(path ?? "").ToLowerInvariant() is ".scss" or ".less";
        var place = sass ? SassPlaceAt(text, regionStart, start) : PlaceAt(text, regionStart, start, lineComments, inAttribute ? 1 : 0);
        string? snippet = place switch
        {
            // Fora das chaves só o que começa por @, como @media.
            Place.TopLevel => abbreviation.StartsWith('@') ? Expand(abbreviation) : null,
            Place.Property => HexColor.IsMatch(abbreviation) || abbreviation == "!" ? null : Expand(abbreviation),
            // Depois do ":", só a cor e o !important.
            Place.Value => abbreviation == "!" ? "!important" : HexColor.IsMatch(abbreviation) ? ColorText(ParseColor(abbreviation[1..])) : null,
            _ => null,
        };
        if (snippet is null)
            return null;
        // m10+p5 num atributo fica numa linha só, separado por espaço, porque quebra de linha não cabe ali.
        if (inAttribute)
            snippet = snippet.Replace("\n", " ");
        else if (sass)
            snippet = ToSass(snippet);
        return new CssExpansion(start, caret - start, abbreviation, snippet);
    }

    // No Sass a linha é a declaração: sem nada antes, com recuo é o nome da propriedade e sem recuo é um
    // seletor. Depois de um ":", é o valor. Depois de outra palavra, como em "nav ul", é um seletor.
    private static Place SassPlaceAt(string text, int lineStart, int start)
    {
        var before = text.AsSpan(lineStart, start - lineStart);
        if (before.IsWhiteSpace())
            return before.Length > 0 ? Place.Property : Place.TopLevel;
        return PlaceAt(text, lineStart, start, lineComments: true, depth: 1) switch
        {
            Place.Value => Place.Value,
            _ => Place.None,
        };
    }

    // Como o Emmet faz para o Sass: sem o ";" no fim e, nos trechos prontos, sem as chaves.
    private static string ToSass(string snippet)
    {
        var lines = new List<string>();
        foreach (var raw in snippet.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim() == "}")
                continue;
            if (line.EndsWith(" {", StringComparison.Ordinal))
                line = line[..^2];
            else if (line.EndsWith(';'))
                line = line[..^1];
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    // Em que ponto do CSS a abreviação começa: fora das chaves, no nome da propriedade ou no valor.
    // Num style="...", o texto já começa dentro de uma regra.
    private static Place PlaceAt(string text, int from, int to, bool lineComments, int depth = 0)
    {
        var sawColon = false;
        char? quote = null;
        var parens = 0;
        for (var i = from; i < to; i++)
        {
            var c = text[i];
            if (quote is { } open)
            {
                if (c == '\\')
                    i++;
                else if (c == open)
                    quote = null;
                continue;
            }
            if (c == '/' && i + 1 < to && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0 || end + 2 > to)
                    return Place.None;
                i = end + 1;
                continue;
            }
            if (lineComments && c == '/' && i + 1 < to && text[i + 1] == '/' && (i == 0 || text[i - 1] != ':'))
            {
                var end = text.IndexOf('\n', i);
                if (end < 0 || end >= to)
                    return Place.None;
                i = end;
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(':
                    parens++;
                    break;
                case ')':
                    parens = Math.Max(0, parens - 1);
                    break;
                case '{':
                    depth++;
                    sawColon = false;
                    break;
                case '}':
                    depth = Math.Max(0, depth - 1);
                    sawColon = false;
                    break;
                case ';':
                    sawColon = false;
                    break;
                case ':' when parens == 0:
                    sawColon = true;
                    break;
            }
        }
        if (quote is not null)
            return Place.None;
        if (depth == 0)
            return Place.TopLevel;
        return sawColon ? Place.Value : Place.Property;
    }

    // Expandir

    // O trecho no formato do VS Code, ou null se a abreviação não bate com nada da tabela.
    public static string? Expand(string abbreviation)
    {
        if (abbreviation.Length == 0 || abbreviation.Length > MaxAbbreviationLength)
            return null;
        var (snippets, properties) = Table.Value;

        // Como no VS Code: uma palavra comprida que é o começo de uma propriedade de verdade, como "displ",
        // fica com a sugestão do servidor de CSS.
        if (abbreviation.Length > 4 && properties.Any(p => p.StartsWith(abbreviation, StringComparison.Ordinal)))
            return null;

        List<CssProperty> parsed;
        try
        {
            parsed = new AbbreviationParser(Tokenize(abbreviation)).Parse();
        }
        catch (FormatException)
        {
            return null;
        }
        if (parsed.Count == 0)
            return null;

        var output = new List<string>();
        foreach (var node in parsed)
        {
            if (Resolve(node, snippets) is not { } line)
                return null;
            output.Add(line);
        }
        return string.Join("\n", output);
    }

    private static string? Resolve(CssProperty node, List<CssSnippet> snippets)
    {
        // lg é o gradiente, como no Emmet.
        if (node.Name == "lg")
            return "background-image: linear-gradient(" + (node.Values.Count > 0 ? string.Join(", ", node.Values.Select(ValueText)) : "${0}") + ")" + Important(node) + ";";

        if (node.Name is null)
        {
            // Só o !, sem propriedade: no lugar do nome não faz sentido, e quem trata o valor é TryExpandAt.
            return null;
        }

        var snippet = FindBestMatch(node.Name, snippets, s => s.Key, MinScore, partialMatch: true);
        return snippet switch
        {
            PropertySnippet property => ResolveProperty(node, property),
            RawSnippet raw => ResolveRaw(node, raw),
            _ => null,
        };
    }

    private static string? ResolveProperty(CssProperty node, PropertySnippet snippet)
    {
        // Em "dib", o "d" é display e o "ib" que sobrou precisa ser uma palavra de display, inline-block.
        var inline = UnmatchedPart(node.Name!, snippet.Key);
        if (inline.Length > 0)
        {
            if (node.Values.Count > 0 || ResolveKeyword(inline, snippet, 0) is not { } keyword)
                return null;
            node.Values.Add([new CssToken(TokenKind.Raw, keyword)]);
        }

        string value;
        if (node.Values.Count > 0)
        {
            foreach (var cssValue in node.Values)
            {
                for (var i = 0; i < cssValue.Count; i++)
                {
                    if (cssValue[i].Kind == TokenKind.Literal && ResolveKeyword(cssValue[i].Text, snippet, 0) is { } keyword)
                        cssValue[i] = new CssToken(TokenKind.Raw, keyword);
                }
            }
            ResolveNumbers(node.Values, snippet.Property);
            value = string.Join(", ", node.Values.Select(ValueText));
        }
        else if (snippet.Alternatives.Count > 0)
        {
            var first = snippet.Alternatives[0];
            // Com várias opções, a primeira entra como campo, para trocar na hora (emmet/emmet#558).
            value = snippet.Alternatives.Count == 1 || first.Contains("${", StringComparison.Ordinal)
                ? first
                : string.Join(" ", SplitAlternative(first).Select((token, i) => $"${{{i + 1}:{token}}}"));
        }
        else
        {
            value = "${0}";
        }
        return snippet.Property + ": " + value + Important(node) + ";";
    }

    // Um trecho pronto, como @media: os valores digitados entram no lugar dos campos, em ordem.
    private static string ResolveRaw(CssProperty node, RawSnippet snippet)
    {
        var values = node.Values.SelectMany(v => v).ToList();
        var next = 0;
        var text = Regex.Replace(snippet.Value, @"\$\{(\d+)(:[^}]+)?\}", match => next < values.Count ? TokenText(values[next++]) : match.Value);
        return text + Important(node);
    }

    private static string Important(CssProperty node) => node.Important ? " !important" : "";

    private static string? ResolveKeyword(string keyword, PropertySnippet snippet, double minScore)
    {
        if (FindBestMatch(keyword, snippet.Keywords.Keys.ToList(), k => k, minScore, partialMatch: false) is { } own)
            return snippet.Keywords[own];
        foreach (var dependency in snippet.Dependencies)
        {
            if (FindBestMatch(keyword, dependency.Keywords.Keys.ToList(), k => k, minScore, partialMatch: false) is { } found)
                return dependency.Keywords[found];
        }
        return FindBestMatch(keyword, GlobalKeywords.ToList(), k => k, minScore, partialMatch: false);
    }

    // 10 vira 10px, 1.5 vira 1.5em, e p, e, x e r são %, em, ex e rem. Sem unidade em z-index e afins.
    private static void ResolveNumbers(List<List<CssToken>> values, string property)
    {
        foreach (var cssValue in values)
        {
            for (var i = 0; i < cssValue.Count; i++)
            {
                var token = cssValue[i];
                if (token.Kind != TokenKind.Number)
                    continue;
                var unit = token.Unit;
                if (unit.Length > 0)
                    unit = UnitAliases.GetValueOrDefault(unit, unit);
                else if (token.Number != 0 && !Unitless.Contains(property))
                    unit = token.Text.Contains('.') ? "em" : "px";
                cssValue[i] = token with { Unit = unit };
            }
        }
    }

    private static string ValueText(List<CssToken> value) => string.Join(" ", value.Select(TokenText));

    private static string TokenText(CssToken token) => token.Kind switch
    {
        TokenKind.Number => token.Number.ToString("0.####", CultureInfo.InvariantCulture) + token.Unit,
        TokenKind.Color => token.Text,
        TokenKind.Raw => token.Text,
        TokenKind.Function => token.Text,
        _ => Escape(token.Text),
    };

    // O que a pessoa digitou vai literal, sem virar campo.
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("$", "\\$").Replace("}", "\\}");

    // A pontuação do Emmet

    private static T? FindBestMatch<T>(string abbreviation, List<T> items, Func<T, string> key, double minScore, bool partialMatch) where T : class
    {
        T? best = null;
        var bestScore = 0.0;
        foreach (var item in items)
        {
            var score = Score(abbreviation, key(item), partialMatch);
            if (score == 1)
                return item;
            if (score > 0 && score >= bestScore)
            {
                bestScore = score;
                best = item;
            }
        }
        return bestScore >= minScore ? best : null;
    }

    // Igual ao score.ts do Emmet: a primeira letra precisa bater, e letras mais perto do começo valem mais.
    public static double Score(string abbreviation, string candidate, bool partialMatch)
    {
        var a = abbreviation.ToLowerInvariant();
        var b = candidate.ToLowerInvariant();
        if (a == b)
            return 1;
        if (a.Length == 0 || b.Length == 0 || a[0] != b[0])
            return 0;
        if (!partialMatch && a.Length > b.Length)
            return 0;

        var minLength = Math.Min(a.Length, b.Length);
        var maxLength = Math.Max(a.Length, b.Length);
        var i = 1;
        var j = 1;
        double score = maxLength;
        while (i < a.Length)
        {
            var found = false;
            var acronym = false;
            while (j < b.Length)
            {
                if (a[i] == b[j])
                {
                    found = true;
                    score += maxLength - (acronym ? i : j);
                    break;
                }
                acronym = b[j] == '-';
                j++;
            }
            if (!found)
            {
                if (!partialMatch)
                    return 0;
                break;
            }
            i++;
        }

        var matchRatio = (double)i / maxLength;
        var delta = maxLength - minLength;
        var maxScore = Sum(maxLength) - Sum(delta);
        return score * matchRatio / maxScore;
    }

    private static double Sum(int n) => n * (n + 1) / 2.0;

    // Em "poas" contra "position", o "as" que não achou lugar.
    private static string UnmatchedPart(string abbreviation, string key)
    {
        var last = 0;
        for (var i = 0; i < abbreviation.Length; i++)
        {
            last = key.IndexOf(abbreviation[i], last);
            if (last < 0)
                return abbreviation[i..];
            last++;
        }
        return "";
    }

    // Cores: #f vira #fff, #e0 vira #e0e0e0, e o curto fica quando dá, como no VS Code.
    private static (int R, int G, int B) ParseColor(string hex)
    {
        string r = "0", g = "0", b = "0";
        switch (hex.Length)
        {
            case 0:
                break;
            case 1:
                r = g = b = hex + hex;
                break;
            case 2:
                r = g = b = hex;
                break;
            case 3:
                (r, g, b) = (new string(hex[0], 2), new string(hex[1], 2), new string(hex[2], 2));
                break;
            default:
                var doubled = hex + hex;
                (r, g, b) = (doubled[..2], doubled[2..4], doubled[4..6]);
                break;
        }
        return (Convert.ToInt32(r, 16), Convert.ToInt32(g, 16), Convert.ToInt32(b, 16));
    }

    private static string ColorText((int R, int G, int B) color)
    {
        static bool Short(int value) => value % 17 == 0;
        return Short(color.R) && Short(color.G) && Short(color.B)
            ? $"#{color.R >> 4:x}{color.G >> 4:x}{color.B >> 4:x}"
            : $"#{color.R:x2}{color.G:x2}{color.B:x2}";
    }

    // Ler a abreviação, como o css-abbreviation do Emmet

    private enum TokenKind { Literal, Number, Color, String, Function, Raw, Operator }

    private sealed record CssToken(TokenKind Kind, string Text, double Number = 0, string Unit = "");

    private sealed class CssProperty
    {
        public string? Name { get; set; }
        public List<List<CssToken>> Values { get; } = [];
        public bool Important { get; set; }
    }

    private static List<CssToken> Tokenize(string abbreviation)
    {
        var tokens = new List<CssToken>();
        var i = 0;
        while (i < abbreviation.Length)
        {
            var start = i;
            var c = abbreviation[i];
            if (ReadNumber(abbreviation, ref i) is { } number)
            {
                var unitStart = i;
                if (i < abbreviation.Length && abbreviation[i] == '%')
                    i++;
                else
                    while (i < abbreviation.Length && (char.IsAsciiLetter(abbreviation[i]) || abbreviation[i] == '_'))
                        i++;
                var unit = abbreviation[unitStart..i];
                tokens.Add(new CssToken(TokenKind.Number, abbreviation[start..unitStart], number, unit));
                // Depois de um número sem unidade, o "-" separa valores, como em m10-20.
                if (unit.Length == 0 && i < abbreviation.Length && abbreviation[i] == '-')
                    tokens.Add(new CssToken(TokenKind.Operator, abbreviation[i++].ToString()));
            }
            else if (c == '#')
            {
                i++;
                var hexStart = i;
                while (i < abbreviation.Length && char.IsAsciiHexDigit(abbreviation[i]))
                    i++;
                tokens.Add(new CssToken(TokenKind.Color, ColorText(ParseColor(abbreviation[hexStart..Math.Min(i, hexStart + 6)]))));
                if (i < abbreviation.Length && abbreviation[i] == '-')
                    tokens.Add(new CssToken(TokenKind.Operator, abbreviation[i++].ToString()));
            }
            else if (c is '"' or '\'')
            {
                var close = abbreviation.IndexOf(c, i + 1);
                i = close < 0 ? abbreviation.Length : close + 1;
                tokens.Add(new CssToken(TokenKind.String, abbreviation[start..i]));
            }
            else if (c == '(')
            {
                // Uma função, como rgba(0,0,0,.5), vai inteira, do jeito que foi escrita.
                var depth = 0;
                while (i < abbreviation.Length)
                {
                    if (abbreviation[i] == '(')
                        depth++;
                    else if (abbreviation[i] == ')' && --depth == 0)
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                if (depth != 0 || tokens.Count == 0 || tokens[^1].Kind != TokenKind.Literal)
                    throw new FormatException();
                tokens[^1] = new CssToken(TokenKind.Function, tokens[^1].Text + abbreviation[start..i]);
            }
            else if (c is '+' or '!' or ',' or ':' or '-')
            {
                i++;
                tokens.Add(new CssToken(TokenKind.Operator, c.ToString()));
            }
            else if (c == '@' || char.IsAsciiLetter(c) || c == '_')
            {
                i++;
                while (i < abbreviation.Length && (char.IsAsciiLetter(abbreviation[i]) || abbreviation[i] is '_' or '%' or '/'))
                    i++;
                tokens.Add(new CssToken(TokenKind.Literal, abbreviation[start..i]));
            }
            else
            {
                throw new FormatException();
            }
        }
        return tokens;
    }

    // -10, 1.5 e .5. O "-" sozinho é separador.
    private static double? ReadNumber(string text, ref int i)
    {
        var start = i;
        if (i < text.Length && text[i] == '-')
            i++;
        var afterSign = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
            i++;
        var hasInteger = i > afterSign;
        if (i < text.Length && text[i] == '.')
        {
            var dot = i++;
            var fractionStart = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
                i++;
            if (!hasInteger && i == fractionStart)
                i = dot;
        }
        if (i == afterSign)
        {
            i = start;
            return null;
        }
        return double.Parse(text.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private sealed class AbbreviationParser(List<CssToken> tokens)
    {
        private int _position;

        private bool AtEnd => _position >= tokens.Count;
        private CssToken Current => tokens[_position];
        private bool IsOperator(string op) => !AtEnd && Current.Kind == TokenKind.Operator && Current.Text == op;

        public List<CssProperty> Parse()
        {
            var result = new List<CssProperty>();
            while (!AtEnd)
            {
                if (ReadProperty() is { } property)
                    result.Add(property);
                else if (IsOperator("+"))
                    _position++;
                else
                    throw new FormatException();
            }
            return result;
        }

        private CssProperty? ReadProperty()
        {
            var property = new CssProperty();
            if (Current.Kind == TokenKind.Literal)
            {
                property.Name = Current.Text;
                _position++;
                if (IsOperator(":") || IsOperator("-"))
                    _position++;
            }

            while (!AtEnd)
            {
                if (IsOperator("!"))
                {
                    property.Important = true;
                    _position++;
                }
                else if (ReadValue() is { } value)
                {
                    property.Values.Add(value);
                }
                else if (IsOperator(","))
                {
                    _position++;
                }
                else
                {
                    break;
                }
            }

            return property.Name is not null || property.Values.Count > 0 || property.Important ? property : null;
        }

        private List<CssToken>? ReadValue()
        {
            var value = new List<CssToken>();
            while (!AtEnd)
            {
                if (Current.Kind is TokenKind.Literal or TokenKind.Number or TokenKind.Color or TokenKind.String or TokenKind.Function)
                {
                    value.Add(Current);
                    _position++;
                }
                else if (IsOperator(":") || IsOperator("-"))
                {
                    _position++;
                }
                else
                {
                    break;
                }
            }
            return value.Count > 0 ? value : null;
        }
    }
}
