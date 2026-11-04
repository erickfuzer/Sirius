using System.Text;

namespace Sirius.Core;

// O resultado de expandir: o trecho a substituir, o texto novo e as paradas do Tab, já no documento.
public sealed record EmmetExpansion(int Start, int Length, string Text, IReadOnlyList<int> Stops);

// Abreviações de HTML, como ul>li*3. Feito aqui, sem servidor: o Tab precisa responder na hora,
// e isto funciona sem Node e sem internet.
public static class Emmet
{
    private const int MaxAbbreviationLength = 500;
    private const int MaxElements = 1000;

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

    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.Ordinal)
    {
        ["btn"] = "button", ["bq"] = "blockquote", ["hdr"] = "header", ["ftr"] = "footer", ["sect"] = "section",
        ["art"] = "article", ["fig"] = "figure", ["figc"] = "figcaption", ["pic"] = "picture", ["str"] = "strong",
        ["ifr"] = "iframe", ["emb"] = "embed", ["obj"] = "object", ["cap"] = "caption", ["colg"] = "colgroup",
        ["fst"] = "fieldset", ["leg"] = "legend", ["sel"] = "select", ["opt"] = "option", ["tarea"] = "textarea", ["acr"] = "abbr",
    };

    private static readonly Dictionary<string, (string Name, string Value)[]> DefaultAttributes = new(StringComparer.Ordinal)
    {
        ["a"] = [("href", "")],
        ["img"] = [("src", ""), ("alt", "")],
        ["input"] = [("type", "text")],
        ["link"] = [("rel", "stylesheet"), ("href", "")],
        ["form"] = [("action", "")],
        ["label"] = [("for", "")],
        ["iframe"] = [("src", ""), ("frameborder", "0")],
        ["audio"] = [("src", "")],
        ["video"] = [("src", "")],
        ["source"] = [("src", ""), ("type", "")],
        ["area"] = [("shape", ""), ("coords", ""), ("href", ""), ("alt", "")],
        ["select"] = [("name", ""), ("id", "")],
        ["textarea"] = [("name", ""), ("id", ""), ("cols", "30"), ("rows", "10")],
        ["meta"] = [("name", ""), ("content", "")],
    };

    public static bool Supports(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() is ".html" or ".htm" or ".php" or ".vue" or ".svelte";

    // A abreviação que termina no cursor, ou null se ali o Tab deve fazer o que sempre fez.
    public static EmmetExpansion? TryExpandAt(string text, int caret, string? path, string indent, string newline)
    {
        if (!Supports(path) || caret <= 0 || caret > text.Length)
            return null;

        var lineStart = text.LastIndexOf('\n', caret - 1) + 1;
        if (FindStart(text, lineStart, caret) is not { } start)
            return null;
        if (!IsPlainMarkup(text, start))
            return null;

        var abbreviation = text[start..caret];
        var leading = LeadingWhitespace(text, lineStart);

        // A página inicial só faz sentido sozinha na linha.
        var isPage = abbreviation is "!" or "html:5";
        if (isPage && start != lineStart + leading.Length)
            return null;

        if (Expand(abbreviation, indent, newline, leading) is not { } expanded)
            return null;
        return new EmmetExpansion(start, caret - start, expanded.Text, expanded.Stops.Select(stop => start + stop).ToList());
    }

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

    private static bool IsPlainMarkup(string text, int offset)
    {
        return !IsOpen(text, offset, "<?", "?>")
            && !IsOpen(text, offset, "<!--", "-->")
            && !IsOpen(text, offset, "<style", "</style")
            && !IsOpen(text, offset, "<script", "</script")
            // Dentro de uma tag, como no valor de um atributo.
            && text.LastIndexOf('<', Math.Max(0, offset - 1)) <= text.LastIndexOf('>', Math.Max(0, offset - 1));
    }

    private static bool IsOpen(string text, int offset, string open, string close)
    {
        if (offset == 0)
            return false;
        var before = text.AsSpan(0, offset);
        return before.LastIndexOf(open, StringComparison.OrdinalIgnoreCase) > before.LastIndexOf(close, StringComparison.OrdinalIgnoreCase);
    }

    public static (string Text, List<int> Stops)? Expand(string abbreviation, string indent = "    ", string newline = "\n", string leading = "")
    {
        if (abbreviation.Length == 0 || abbreviation.Length > MaxAbbreviationLength)
            return null;
        if (abbreviation is "!" or "html:5")
            return Page(indent, newline, leading);

        List<Node>? nodes;
        try
        {
            nodes = new Parser(abbreviation).Parse();
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
        return (writer.Text, writer.Stops);
    }

    private static (string, List<int>) Page(string indent, string newline, string leading)
    {
        const string Before = "<!DOCTYPE html>\n<html lang=\"pt-BR\">\n<head>\n\t<meta charset=\"UTF-8\">\n\t<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n\t<title>";
        const string Title = "Documento";
        const string Middle = "</title>\n</head>\n<body>\n\t";
        const string After = "\n</body>\n</html>";

        string Format(string part) => part.Replace("\t", indent).Replace("\n", newline + leading);

        var text = new StringBuilder(Format(Before));
        var stops = new List<int> { text.Length };
        text.Append(Title).Append(Format(Middle));
        stops.Add(text.Length);
        text.Append(Format(After));
        return (text.ToString(), stops);
    }

    // A árvore da abreviação, com cada repetição já aberta em elementos de verdade.

    private sealed class Node
    {
        public string? Name { get; set; }
        public List<(string Name, string Value)> Attributes { get; } = [];
        public string? Text { get; set; }
        public int Repeat { get; set; } = 1;
        public bool Repeats { get; set; }
        public bool IsGroup { get; init; }
        public List<Node> Children { get; set; } = [];
    }

    private sealed class Element
    {
        public string? Name { get; init; }
        public List<(string Name, string Value)> Attributes { get; init; } = [];
        public string? Text { get; init; }
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
                if (name is not null && !IsAllowedName(name))
                    return null;

                var children = Instantiate(node.Children, name, current, ref count);
                if (children is null)
                    return null;

                result.Add(new Element
                {
                    Name = name,
                    Attributes = name is null ? [] : BuildAttributes(name, node, current),
                    Text = node.Text is null ? null : Number(node.Text, current),
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

    private static List<(string Name, string Value)> BuildAttributes(string name, Node node, int number)
    {
        var attributes = new List<(string Name, string Value)>();
        if (DefaultAttributes.TryGetValue(name, out var defaults))
            attributes.AddRange(defaults);

        foreach (var (attribute, value) in node.Attributes)
        {
            var numbered = Number(value, number);
            var index = attributes.FindIndex(existing => existing.Name == attribute);
            if (index < 0)
                attributes.Add((attribute, numbered));
            else if (attribute == "class" && attributes[index].Value.Length > 0)
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

        public string Text => _text.ToString();
        public List<int> Stops { get; } = [];

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

        private void Write(Element element, int level)
        {
            if (element.Name is null)
            {
                _text.Append(element.Text);
                return;
            }

            _text.Append('<').Append(element.Name);
            foreach (var (name, value) in element.Attributes)
            {
                _text.Append(' ').Append(name).Append("=\"");
                if (value.Length == 0)
                    Stops.Add(_text.Length);
                _text.Append(value).Append('"');
            }
            _text.Append('>');

            if (VoidTags.Contains(element.Name))
                return;

            _text.Append(element.Text);
            if (element.Children.Count == 0)
            {
                if (element.Text is null)
                    Stops.Add(_text.Length);
            }
            else if (FitsOnOneLine(element))
            {
                foreach (var child in element.Children)
                    Write(child, level);
            }
            else
            {
                foreach (var child in element.Children)
                {
                    NewLine(level + 1);
                    Write(child, level + 1);
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

    // Lê a abreviação. Qualquer coisa fora do formato cancela tudo, e o Tab fica como sempre foi.
    private sealed class Parser(string text)
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
                        if (item.IsGroup)
                            throw new FormatException();
                        levels.Push(item.Children);
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
                node = ParseElement();
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
            var node = new Node();
            var name = ReadName();
            if (name.Length > 0)
                ApplyName(node, name);

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

        private static void ApplyName(Node node, string name)
        {
            var colon = name.IndexOf(':');
            if (colon > 0)
            {
                var (tag, kind) = (name[..colon], name[(colon + 1)..]);
                switch (tag)
                {
                    case "input" when kind.Length > 0:
                        node.Name = "input";
                        node.Attributes.Add(("type", kind));
                        return;
                    case "link" when kind == "css":
                        node.Name = "link";
                        node.Attributes.Add(("href", "style.css"));
                        return;
                    case "script" when kind == "src":
                        node.Name = "script";
                        node.Attributes.Add(("src", ""));
                        return;
                    case "a" when kind == "link":
                        node.Name = "a";
                        node.Attributes.Add(("href", "http://"));
                        return;
                    case "a" when kind == "mail":
                        node.Name = "a";
                        node.Attributes.Add(("href", "mailto:"));
                        return;
                    default:
                        throw new FormatException();
                }
            }
            node.Name = ShortNames.GetValueOrDefault(name, name);
        }

        private static void AddClass(Node node, string name)
        {
            var index = node.Attributes.FindIndex(attribute => attribute.Name == "class");
            if (index < 0)
                node.Attributes.Add(("class", name));
            else
                node.Attributes[index] = ("class", node.Attributes[index].Value + " " + name);
        }

        // Só minúsculas: com maiúscula é texto, como "Olá" ou "Usuario".
        private string ReadName()
        {
            var start = _position;
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
