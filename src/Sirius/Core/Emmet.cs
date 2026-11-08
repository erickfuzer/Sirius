using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// O resultado de expandir: o trecho a substituir e o texto novo, com os campos do Tab contados a partir de Start.
public sealed record EmmetExpansion(int Start, int Length, SnippetText Snippet)
{
    public string Text => Snippet.Text;
}

// Abreviações de HTML, como ul>li*3. Feito aqui, sem servidor: a sugestão precisa aparecer na hora,
// e isto funciona sem Node e sem internet. Os atalhos com nome, como a:link e !, vêm da tabela oficial
// do Emmet (Assets\Snippets\emmet-html.json, licença MIT), a mesma do VS Code.
public static class Emmet
{
    private const int MaxAbbreviationLength = 500;
    private const int MaxElements = 1000;
    private const int MaxDepth = 10;
    private const int MaxSuggestions = 50;
    public const string PageTitle = "Documento";

    // Os campos da tabela, como ${1:style}, viram marcas fora do texto comum, para o leitor não confundir
    // a chave do campo com a do texto nem o $ com o da numeração.
    private const char FieldOpen = '';
    private const char FieldClose = '';
    private const char FieldSeparator = '';
    // Atributo sem valor, como o disabled de button:d.
    private const string BooleanValue = "";

    private static readonly HashSet<string> KnownTags = new(StringComparer.Ordinal)
    {
        "a", "abbr", "address", "area", "article", "aside", "audio", "b", "base", "bdi", "bdo", "blockquote", "body", "br", "button",
        "canvas", "caption", "cite", "code", "col", "colgroup", "data", "datalist", "dd", "del", "details", "dfn", "dialog", "div", "dl", "dt",
        "em", "embed", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hgroup", "hr",
        "html", "i", "iframe", "img", "input", "ins", "kbd", "label", "legend", "li", "link", "main", "map", "mark", "menu", "meta", "meter",
        "nav", "noscript", "object", "ol", "optgroup", "option", "output", "p", "picture", "pre", "progress", "q", "rp", "rt", "ruby", "s",
        "samp", "script", "search", "section", "select", "slot", "small", "source", "span", "strong", "style", "sub", "summary", "sup", "svg",
        "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead", "time", "title", "tr", "track", "u", "ul", "var", "video", "wbr",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    private static readonly HashSet<string> InlineTags = new(StringComparer.Ordinal)
    {
        "a", "abbr", "b", "bdi", "bdo", "br", "button", "cite", "code", "data", "del", "dfn", "em", "i", "img", "input", "ins", "kbd", "label",
        "mark", "q", "s", "samp", "select", "small", "span", "strong", "sub", "sup", "textarea", "time", "u", "var", "wbr",
    };

    private static readonly Lazy<Dictionary<string, string>> Table = new(LoadTable);
    private static readonly Regex TableField = new(@"\$\{(\d+)(?::([^}]*))?\}", RegexOptions.CultureInvariant);

    // Cada chave pode ter apelidos, como "opt|option". O título e a língua da página vêm em português.
    private static Dictionary<string, string> LoadTable()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = SnippetLibrary.Resource("emmet-html.json");
        if (stream is null)
            return table;
        using var document = JsonDocument.Parse(stream);
        foreach (var item in document.RootElement.EnumerateObject())
        {
            var value = (item.Value.GetString() ?? "")
                .Replace("${lang}", "pt-BR")
                .Replace("${charset}", "UTF-8")
                .Replace("${1:Document}", "${1:" + PageTitle + "}");
            value = TableField.Replace(value, match => Field(int.Parse(match.Groups[1].Value), match.Groups[2].Value));
            foreach (var key in item.Name.Split('|'))
                table[key.Trim()] = value;
        }
        return table;
    }

    private static string Field(int index, string placeholder) => $"{FieldOpen}{index}{FieldSeparator}{placeholder}{FieldClose}";

    public static bool Supports(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() is ".html" or ".htm" or ".php" or ".vue" or ".svelte";

    // A abreviação que termina no cursor, ou null se ali não há abreviação.
    public static EmmetExpansion? TryExpandAt(string text, int caret, string? path, string indent, string newline) =>
        SuggestionsAt(text, caret, path, indent, newline, withTable: false).Select(s => s.Expansion).FirstOrDefault();

    // O que a lista mostra, como no VS Code: a abreviação digitada e, depois dela, as chaves da tabela que
    // começam pelo último nome, como a:link e a:mail para "a:", ou !!! para "!".
    public static List<(string Label, EmmetExpansion Expansion)> SuggestionsAt(string text, int caret, string? path, string indent, string newline, bool withTable = true)
    {
        var result = new List<(string, EmmetExpansion)>();
        if (!Supports(path) || caret <= 0 || caret > text.Length)
            return result;

        var lineStart = text.LastIndexOf('\n', caret - 1) + 1;
        var end = PastClosers(text, lineStart, caret);
        if (FindStart(text, lineStart, end) is not { } start || !IsPlainMarkup(text, start))
            return result;

        var abbreviation = text[start..end];
        var leading = LeadingWhitespace(text, lineStart);
        var aloneOnLine = start == lineStart + leading.Length;

        void Add(string label, string candidate)
        {
            // A página inicial só faz sentido sozinha na linha.
            if (candidate is "!" or "html:5" && !aloneOnLine)
                return;
            if (Expand(candidate, indent, newline, leading) is { } expanded)
                result.Add((label, new EmmetExpansion(start, end - start, expanded)));
        }

        Add(abbreviation, abbreviation);
        if (!withTable)
            return result;

        // Como no VS Code, só para um nome sozinho: o filtro da lista compara a abreviação inteira com a chave,
        // e "ul>li" não bate com "link".
        if (!abbreviation.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '!' or '-'))
            return result;
        foreach (var key in Table.Value.Keys.Where(key => key.Length > abbreviation.Length && key.StartsWith(abbreviation, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            if (result.Count >= MaxSuggestions)
                break;
            Add(key, key);
        }
        return result;
    }

    // O fechamento automático deixa o cursor antes do que a própria abreviação precisa, como em div{oi|}.
    private static int PastClosers(string text, int lineStart, int caret)
    {
        var open = new Stack<char>();
        for (var i = lineStart; i < caret; i++)
        {
            var c = text[i];
            if (open.TryPeek(out var top) && top is '"' or '\'')
            {
                if (c == top)
                    open.Pop();
            }
            else if (AutoPairs.CloserFor(c) is { } closer)
            {
                open.Push(closer);
            }
            else if (open.TryPeek(out top) && c == top)
            {
                open.Pop();
            }
        }

        var end = caret;
        while (end < text.Length && open.TryPeek(out var closer) && text[end] == closer)
        {
            open.Pop();
            end++;
        }
        return end;
    }

    // O "!" sozinho na linha é um pedido claro da página inicial, mesmo quando ela não pode vir aqui.
    public static bool IsPageRequest(string text, int caret)
    {
        if (caret <= 0 || caret > text.Length)
            return false;
        var lineStart = text.LastIndexOf('\n', caret - 1) + 1;
        return text.AsSpan(lineStart, caret - lineStart).Trim() is "!";
    }

    public static bool IsVoidTag(string name) => VoidTags.Contains(name.ToLowerInvariant());

    private static string LeadingWhitespace(string text, int lineStart)
    {
        var end = lineStart;
        while (end < text.Length && text[end] is ' ' or '\t')
            end++;
        return text[lineStart..end];
    }

    // Só vale no começo da linha ou logo depois de uma tag: assim, "Olá mundo" nunca vira <mundo>.
    private static int? FindStart(string text, int lineStart, int caret)
    {
        var depth = 0;
        var start = lineStart;
        for (var i = caret - 1; i >= lineStart; i--)
        {
            var c = text[i];
            if (c is ']' or '}' or ')')
            {
                depth++;
            }
            else if (c is '[' or '{' or '(')
            {
                if (--depth < 0)
                    return null;
            }
            else if (depth == 0 && char.IsWhiteSpace(c))
            {
                start = i + 1;
                break;
            }
            else if (depth == 0 && c == '<')
            {
                // Em <div>ul>li, o primeiro ">" fecha a tag, e a abreviação vem depois dele.
                var close = text.IndexOf('>', i, caret - i);
                if (close < 0)
                    return null;
                start = close + 1;
                break;
            }
        }

        if (depth != 0 || start >= caret)
            return null;

        var before = start - 1;
        while (before >= lineStart && char.IsWhiteSpace(text[before]))
            before--;
        return before < lineStart || text[before] == '>' ? start : null;
    }

    public static bool IsPlainMarkup(string text, int offset)
    {
        return !Markup.InPhp(text, offset)
            && !Markup.InComment(text, offset)
            && !Markup.InStyle(text, offset)
            && !Markup.InScript(text, offset)
            // Dentro de uma tag, como no valor de um atributo.
            && (offset == 0 || text.LastIndexOf('<', offset - 1) <= text.LastIndexOf('>', offset - 1));
    }

    public static SnippetText? Expand(string abbreviation, string indent = "    ", string newline = "\n", string leading = "")
    {
        if (abbreviation.Length == 0 || abbreviation.Length > MaxAbbreviationLength || abbreviation.Any(c => c is >= FieldOpen and <= ''))
            return null;

        List<Node>? nodes;
        try
        {
            nodes = new Parser(abbreviation, [], trusted: false).Parse();
        }
        catch (FormatException)
        {
            return null;
        }
        if (nodes is null or { Count: 0 })
            return null;

        var elements = 0;
        var concrete = Instantiate(nodes, null, 0, ref elements);
        if (concrete is null)
            return null;

        var writer = new Writer(indent, newline, leading);
        writer.WriteAll(concrete);
        return writer.Result;
    }

    // A árvore da abreviação, com cada repetição já aberta em elementos de verdade.

    private sealed class Node
    {
        public string? Name { get; set; }
        public List<(string Name, string Value)> Attributes { get; set; } = [];
        public string? Text { get; set; }
        public int Repeat { get; set; } = 1;
        public bool Repeats { get; set; }
        public bool IsGroup { get; init; }
        public bool SelfClosing { get; set; }
        // O nome veio da tabela, como acronym ou marquee, que a lista de tags conhecidas não tem.
        public bool Trusted { get; init; }
        // Onde entram os filhos que a pessoa escreveu depois de um atalho: no elemento mais fundo dele.
        public Node? Deepest { get; set; }
        public List<Node> Children { get; set; } = [];
    }

    private sealed class Element
    {
        public string? Name { get; init; }
        public List<(string Name, string Value)> Attributes { get; init; } = [];
        public string? Text { get; init; }
        public bool SelfClosing { get; init; }
        public List<Element> Children { get; init; } = [];
    }

    private static List<Element>? Instantiate(List<Node> nodes, string? parent, int number, ref int count)
    {
        var result = new List<Element>();
        foreach (var node in nodes)
        {
            for (var i = 1; i <= node.Repeat; i++)
            {
                // O $ vale o número da repetição mais próxima.
                var current = node.Repeats ? i : number;
                if (node.IsGroup)
                {
                    var inner = Instantiate(node.Children, parent, current, ref count);
                    if (inner is null)
                        return null;
                    result.AddRange(inner);
                    continue;
                }

                if (++count > MaxElements)
                    return null;

                var name = node.Name is null ? (node.Attributes.Count == 0 && node.Text is not null ? null : ImplicitName(parent)) : Number(node.Name, current);
                if (name is not null && !node.Trusted && !IsAllowedName(name))
                    return null;

                var children = Instantiate(node.Children, name, current, ref count);
                if (children is null)
                    return null;

                result.Add(new Element
                {
                    Name = name,
                    Attributes = name is null ? [] : MergeAttributes(node, current),
                    Text = node.Text is null ? null : Number(node.Text, current),
                    SelfClosing = node.SelfClosing,
                    Children = children,
                });
            }
        }
        return result;
    }

    private static string ImplicitName(string? parent) => parent switch
    {
        "ul" or "ol" or "menu" => "li",
        "table" or "thead" or "tbody" or "tfoot" => "tr",
        "tr" => "td",
        "select" or "optgroup" or "datalist" => "option",
        "picture" => "source",
        "colgroup" => "col",
        "dl" => "dt",
        _ when parent is not null && InlineTags.Contains(parent) => "span",
        _ => "div",
    };

    private static bool IsAllowedName(string name) => KnownTags.Contains(name) || name.Contains('-');

    // Os da tabela vêm primeiro e os da pessoa depois. O mesmo atributo repetido fica no lugar do primeiro,
    // com o último valor, e as classes se somam.
    private static List<(string Name, string Value)> MergeAttributes(Node node, int number)
    {
        var attributes = new List<(string Name, string Value)>();
        foreach (var (attribute, value) in node.Attributes)
        {
            var numbered = Number(value, number);
            var index = attributes.FindIndex(existing => existing.Name == attribute);
            if (index < 0)
                attributes.Add((attribute, numbered));
            else if (attribute == "class" && attributes[index].Value.Length > 0 && numbered.Length > 0)
                attributes[index] = (attribute, attributes[index].Value + " " + numbered);
            else
                attributes[index] = (attribute, numbered);
        }
        return attributes;
    }

    // $ vira o número, e $$ completa com zeros, como 01 e 02.
    private static string Number(string text, int number)
    {
        if (number == 0 || !text.Contains('$'))
            return text;
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '$')
            {
                result.Append(text[i]);
                continue;
            }
            var width = 1;
            while (i + 1 < text.Length && text[i + 1] == '$')
            {
                width++;
                i++;
            }
            result.Append(number.ToString().PadLeft(width, '0'));
        }
        return result.ToString();
    }

    private sealed class Writer(string indent, string newline, string leading)
    {
        private readonly StringBuilder _text = new();
        private readonly List<SnippetStop> _stops = [];
        private int _field = 1;

        public SnippetText Result => new(_text.ToString(), _stops);

        public void WriteAll(List<Element> elements)
        {
            for (var i = 0; i < elements.Count; i++)
            {
                if (i > 0)
                    NewLine(0);
                Write(elements[i], 0);
            }
        }

        private void NewLine(int level)
        {
            _text.Append(newline).Append(leading);
            for (var i = 0; i < level; i++)
                _text.Append(indent);
        }

        // Como no Emmet: cada valor numera os campos dele a partir do contador, que depois passa do maior.
        // Assim os campos seguem a ordem do texto, e um valor vazio é um campo novo.
        private void WriteValue(string value, bool fieldWhenEmpty)
        {
            if (value.Length == 0)
            {
                if (fieldWhenEmpty)
                    _stops.Add(new SnippetStop(_field++, _text.Length, 0));
                return;
            }

            var largest = -1;
            var at = 0;
            while (at < value.Length)
            {
                var open = value.IndexOf(FieldOpen, at);
                if (open < 0)
                {
                    _text.Append(value, at, value.Length - at);
                    break;
                }
                _text.Append(value, at, open - at);
                var separator = value.IndexOf(FieldSeparator, open);
                var close = value.IndexOf(FieldClose, separator);
                var index = int.Parse(value.AsSpan(open + 1, separator - open - 1));
                var placeholder = value[(separator + 1)..close];
                _stops.Add(new SnippetStop(_field + index, _text.Length, placeholder.Length));
                _text.Append(placeholder);
                largest = Math.Max(largest, index);
                at = close + 1;
            }
            if (largest >= 0)
                _field += largest + 1;
        }

        private void Write(Element element, int level)
        {
            if (element.Name is null)
            {
                WriteValue(element.Text ?? "", fieldWhenEmpty: false);
                return;
            }

            _text.Append('<').Append(element.Name);
            foreach (var (name, value) in element.Attributes)
            {
                _text.Append(' ').Append(name);
                if (value == BooleanValue)
                    continue;
                _text.Append("=\"");
                WriteValue(value, fieldWhenEmpty: true);
                _text.Append('"');
            }
            _text.Append('>');

            if (element.SelfClosing || VoidTags.Contains(element.Name))
                return;

            if (element.Text is not null)
                WriteValue(element.Text, fieldWhenEmpty: false);
            if (element.Children.Count == 0)
            {
                if (element.Text is null && element.Name == "body")
                {
                    // O corpo vazio da página abre uma linha para o cursor, como no VS Code.
                    NewLine(level + 1);
                    WriteValue("", fieldWhenEmpty: true);
                    NewLine(level);
                }
                else if (element.Text is null)
                {
                    WriteValue("", fieldWhenEmpty: true);
                }
            }
            else if (FitsOnOneLine(element))
            {
                foreach (var child in element.Children)
                    Write(child, level);
            }
            else
            {
                // O head e o body ficam rentes ao html, sem recuo, como no VS Code.
                var inner = element.Name == "html" ? level : level + 1;
                foreach (var child in element.Children)
                {
                    NewLine(inner);
                    Write(child, inner);
                }
                NewLine(level);
            }
            _text.Append("</").Append(element.Name).Append('>');
        }

        // Poucos elementos em linha continuam juntos, como li>a. A partir de três, um por linha.
        private static bool FitsOnOneLine(Element element) =>
            element.Children.Count < 3
            && element.Children.All(child => child.Name is null || (InlineTags.Contains(child.Name) && (child.Children.Count == 0 || FitsOnOneLine(child))));
    }

    // Lê a abreviação. Qualquer coisa fora do formato cancela tudo, e nada é sugerido.
    // "trusted" é o texto de um atalho da tabela, que pode ter campos e nomes de tag antigos.
    private sealed class Parser(string text, List<string> stack, bool trusted)
    {
        private int _position;

        private bool AtEnd => _position >= text.Length;
        private char Current => text[_position];

        public List<Node>? Parse()
        {
            var nodes = ParseChain();
            return AtEnd ? nodes : null;
        }

        private List<Node> ParseChain()
        {
            var root = new List<Node>();
            var levels = new Stack<List<Node>>();
            levels.Push(root);

            while (true)
            {
                var item = ParseItem();
                levels.Peek().Add(item);
                if (AtEnd || Current == ')')
                    return root;

                switch (Current)
                {
                    case '>':
                        _position++;
                        var parent = item.Deepest ?? item;
                        if (parent.IsGroup)
                            throw new FormatException();
                        levels.Push(parent.Children);
                        break;
                    case '+':
                        _position++;
                        break;
                    case '^':
                        while (!AtEnd && Current == '^')
                        {
                            _position++;
                            if (levels.Count > 1)
                                levels.Pop();
                        }
                        break;
                    default:
                        throw new FormatException();
                }
            }
        }

        private Node ParseItem()
        {
            if (AtEnd)
                throw new FormatException();

            Node node;
            if (Current == '(')
            {
                _position++;
                node = new Node { IsGroup = true, Children = ParseChain() };
                if (AtEnd || Current != ')')
                    throw new FormatException();
                _position++;
            }
            else
            {
                node = Resolve(ParseElement());
            }

            if (!AtEnd && Current == '*')
                ReadRepeat(node);
            return node;
        }

        private void ReadRepeat(Node node)
        {
            _position++;
            var start = _position;
            while (!AtEnd && char.IsAsciiDigit(Current))
                _position++;
            if (start == _position || !int.TryParse(text.AsSpan(start, _position - start), out var repeat) || repeat is < 1 or > MaxElements)
                throw new FormatException();
            node.Repeat = repeat;
            node.Repeats = true;
        }

        private Node ParseElement()
        {
            var node = new Node { Trusted = trusted };
            var name = ReadName();
            if (name.Length > 0)
                node.Name = name;

            var any = name.Length > 0;
            while (!AtEnd)
            {
                switch (Current)
                {
                    case '.':
                        _position++;
                        AddClass(node, ReadPart());
                        break;
                    case '#':
                        _position++;
                        node.Attributes.Add(("id", ReadPart()));
                        break;
                    case '[':
                        ReadAttributes(node);
                        break;
                    case '{':
                        node.Text = ReadText();
                        break;
                    case '/' when any:
                        _position++;
                        node.SelfClosing = true;
                        break;
                    case '*' when any:
                        // Em qualquer ordem: li*2{x} e li{x}*2 são a mesma coisa.
                        ReadRepeat(node);
                        break;
                    default:
                        if (!any)
                            throw new FormatException();
                        return node;
                }
                any = true;
            }
            if (!any)
                throw new FormatException();
            return node;
        }

        // Troca um nome da tabela, como a:link, pelo que ele quer dizer, como no próprio Emmet: os atributos
        // e o texto da pessoa vão para o atalho, e um atalho que cita a si mesmo, como "img", para ali.
        private Node Resolve(Node node)
        {
            if (node.Name is not { } name || !Table.Value.TryGetValue(name, out var snippet) || stack.Contains(snippet))
                return node;
            if (stack.Count >= MaxDepth)
                throw new FormatException();

            stack.Add(snippet);
            List<Node>? nodes;
            try
            {
                nodes = new Parser(snippet, stack, trusted: true).Parse();
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }
            if (nodes is null or { Count: 0 })
                throw new FormatException();

            foreach (var top in nodes)
            {
                top.Attributes = [.. top.Attributes, .. node.Attributes];
                if (node.Text is not null)
                    top.Text = node.Text;
                if (node.Repeats)
                {
                    top.Repeat = node.Repeat;
                    top.Repeats = true;
                }
                if (node.SelfClosing)
                    top.SelfClosing = true;
            }

            var result = nodes.Count == 1 ? nodes[0] : new Node { IsGroup = true, Trusted = true, Children = nodes };
            var deepest = result;
            while (deepest.Children.Count > 0)
                deepest = deepest.Children[^1];
            result.Deepest = deepest;
            return result;
        }

        private static void AddClass(Node node, string name)
        {
            var index = node.Attributes.FindIndex(attribute => attribute.Name == "class");
            if (index < 0)
                node.Attributes.Add(("class", name));
            else
                node.Attributes[index] = ("class", node.Attributes[index].Value + " " + name);
        }

        // Só minúsculas: com maiúscula é texto, como "Olá" ou "Usuario". O "!" e o "!!!" são da tabela.
        private string ReadName()
        {
            var start = _position;
            if (!AtEnd && Current == '!')
            {
                while (!AtEnd && Current == '!')
                    _position++;
                return text[start.._position];
            }
            if (AtEnd || !(char.IsAsciiLetterLower(Current)))
                return "";
            while (!AtEnd && (char.IsAsciiLetterLower(Current) || char.IsAsciiDigit(Current) || Current is '-' or ':' or '$'))
                _position++;
            return text[start.._position];
        }

        private string ReadPart()
        {
            var start = _position;
            if (AtEnd || !(char.IsAsciiLetter(Current) || Current is '_' or '-' or '$'))
                throw new FormatException();
            while (!AtEnd && (char.IsAsciiLetterOrDigit(Current) || Current is '_' or '-' or '$' or ':'))
                _position++;
            return text[start.._position];
        }

        private string ReadText()
        {
            var close = text.IndexOf('}', _position);
            if (close < 0)
                throw new FormatException();
            var value = text[(_position + 1)..close];
            _position = close + 1;
            return value;
        }

        private void ReadAttributes(Node node)
        {
            _position++;
            while (true)
            {
                while (!AtEnd && Current == ' ')
                    _position++;
                if (AtEnd)
                    throw new FormatException();
                if (Current == ']')
                {
                    _position++;
                    return;
                }

                var start = _position;
                while (!AtEnd && (char.IsAsciiLetterOrDigit(Current) || Current is '-' or '_' or ':' or '@'))
                    _position++;
                if (start == _position)
                    throw new FormatException();
                var name = text[start.._position];

                var value = "";
                if (!AtEnd && Current == '=')
                {
                    _position++;
                    value = ReadAttributeValue();
                }
                else if (!AtEnd && Current == '.')
                {
                    // [disabled.]: o atributo sozinho, sem ="".
                    _position++;
                    value = BooleanValue;
                }
                node.Attributes.Add((name, value));
            }
        }

        private string ReadAttributeValue()
        {
            if (AtEnd)
                throw new FormatException();
            if (Current is '"' or '\'')
            {
                var close = text.IndexOf(Current, _position + 1);
                if (close < 0)
                    throw new FormatException();
                var quoted = text[(_position + 1)..close];
                _position = close + 1;
                return quoted;
            }

            var start = _position;
            while (!AtEnd && Current is not (' ' or ']'))
                _position++;
            return text[start.._position];
        }
    }
}
