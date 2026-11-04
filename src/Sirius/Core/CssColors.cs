using System.Globalization;
using System.Text.RegularExpressions;

namespace Sirius.Core;

public enum CssColorFormat { Hex, Rgb, Hsl, Named }

public readonly record struct CssColor(byte R, byte G, byte B, double A = 1)
{
    // Tom de 0 a 360; saturação e brilho de 0 a 1.
    public static CssColor FromHsv(double hue, double saturation, double value, double alpha = 1)
    {
        var chroma = value * saturation;
        var section = (hue % 360 + 360) % 360 / 60;
        var x = chroma * (1 - Math.Abs(section % 2 - 1));
        var (r, g, b) = (int)section switch
        {
            0 => (chroma, x, 0d),
            1 => (x, chroma, 0d),
            2 => (0d, chroma, x),
            3 => (0d, x, chroma),
            4 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        var m = value - chroma;
        return new CssColor(ToByte(r + m), ToByte(g + m), ToByte(b + m), Math.Clamp(alpha, 0, 1));
    }

    public (double Hue, double Saturation, double Value) ToHsv()
    {
        var (r, g, b) = (R / 255d, G / 255d, B / 255d);
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        return (HueOf(r, g, b, max, delta), max == 0 ? 0 : delta / max, max);
    }

    public (double Hue, double Saturation, double Lightness) ToHsl()
    {
        var (r, g, b) = (R / 255d, G / 255d, B / 255d);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var lightness = (max + min) / 2;
        var saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
        return (HueOf(r, g, b, max, delta), saturation, lightness);
    }

    public static CssColor FromHsl(double hue, double saturation, double lightness, double alpha = 1)
    {
        var value = lightness + saturation * Math.Min(lightness, 1 - lightness);
        var hsvSaturation = value == 0 ? 0 : 2 * (1 - lightness / value);
        return FromHsv(hue, hsvSaturation, value, alpha);
    }

    private static double HueOf(double r, double g, double b, double max, double delta)
    {
        if (delta == 0)
            return 0;
        var hue = max == r ? (g - b) / delta % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return (hue * 60 + 360) % 360;
    }

    public static byte ToByte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
}

public readonly record struct CssColorMatch(int Start, int Length, CssColor Color, CssColorFormat Format);

// Acha as cores de um arquivo só onde elas são cores. Fora de um valor, #add é um seletor,
// e em HTML é um link: por isso o texto é percorrido, em vez de procurar qualquer #abc.
public static partial class CssColors
{
    private const int MaxFileLength = 2_000_000;
    private const int MaxFunctionLength = 64;

    [GeneratedRegex(@"<style\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleTag();

    [GeneratedRegex(@"(?<=<[^<>]*)\sstyle\s*=\s*([""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex(@"[\s,/]+")]
    private static partial Regex ArgumentSeparator();

    private enum FileKind { None, Css, CssWithLineComments, Sass, Markup }

    private static FileKind KindOf(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
    {
        ".css" => FileKind.Css,
        ".scss" or ".less" => FileKind.CssWithLineComments,
        ".sass" => FileKind.Sass,
        ".html" or ".htm" or ".php" or ".vue" or ".svelte" => FileKind.Markup,
        _ => FileKind.None,
    };

    public static bool Supports(string? path) => KindOf(path) != FileKind.None;

    public static IReadOnlyList<CssColorMatch> Find(string text, string? path)
    {
        var kind = KindOf(path);
        if (kind == FileKind.None || text.Length > MaxFileLength)
            return [];

        var found = new List<CssColorMatch>();
        switch (kind)
        {
            case FileKind.Markup:
                FindInMarkup(text, found);
                found.Sort((a, b) => a.Start.CompareTo(b.Start));
                break;
            case FileKind.Css:
                Scan(text, 0, text.Length, lineComments: false, newlineEnds: false, found);
                break;
            case FileKind.CssWithLineComments:
                Scan(text, 0, text.Length, lineComments: true, newlineEnds: false, found);
                break;
            case FileKind.Sass:
                Scan(text, 0, text.Length, lineComments: true, newlineEnds: true, found);
                break;
        }
        return found;
    }

    private static void FindInMarkup(string text, List<CssColorMatch> found)
    {
        foreach (Match tag in StyleTag().Matches(text))
        {
            var start = tag.Index + tag.Length;
            var close = text.IndexOf("</style", start, StringComparison.OrdinalIgnoreCase);
            Scan(text, start, close < 0 ? text.Length : close, lineComments: false, newlineEnds: false, found);
        }

        foreach (Match attribute in StyleAttribute().Matches(text))
        {
            var start = attribute.Index + attribute.Length;
            var close = text.IndexOf(attribute.Groups[1].Value[0], start);
            if (close >= 0)
                Scan(text, start, close, lineComments: false, newlineEnds: false, found);
        }
    }

    private static void Scan(string t, int start, int end, bool lineComments, bool newlineEnds, List<CssColorMatch> found)
    {
        var pending = new List<CssColorMatch>();
        var inValue = false;
        var property = "";
        var statementStart = start;

        void EndStatement(int next)
        {
            if (inValue)
                found.AddRange(pending);
            pending.Clear();
            inValue = false;
            statementStart = next;
        }

        for (var i = start; i < end; i++)
        {
            var c = t[i];
            var next = i + 1 < end ? t[i + 1] : '\0';

            if (c == '/' && next == '*')
            {
                var close = t.IndexOf("*/", i + 2, end - i - 2, StringComparison.Ordinal);
                i = close < 0 ? end : close + 1;
            }
            else if (c == '/' && next == '/' && lineComments)
            {
                var close = t.IndexOf('\n', i, end - i);
                i = close < 0 ? end : close - 1;
            }
            else if (c is '"' or '\'')
            {
                i = SkipString(t, i, end);
            }
            else if (c == '<' && next == '?')
            {
                // Um trecho de PHP dentro do estilo não é CSS.
                var close = t.IndexOf("?>", i + 2, end - i - 2, StringComparison.Ordinal);
                i = close < 0 ? end : close + 1;
            }
            else if (c == '#' && next == '{')
            {
                // Interpolação do SCSS.
                var close = t.IndexOf('}', i, end - i);
                i = close < 0 ? end : close;
            }
            else if (c == '{')
            {
                // O que veio antes era um seletor, mesmo com ":", como em a:hover #fff.
                pending.Clear();
                inValue = false;
                statementStart = i + 1;
            }
            else if (c is '}' or ';' || (c == '\n' && newlineEnds))
            {
                EndStatement(i + 1);
            }
            else if (c == ':' && !inValue)
            {
                inValue = true;
                property = t[statementStart..i].Trim().ToLowerInvariant();
            }
            else if (inValue && c == '#')
            {
                i = ReadHex(t, i, end, pending);
            }
            else if (inValue && char.IsAsciiLetter(c) && (i == start || !IsNamePart(t[i - 1])))
            {
                i = ReadName(t, i, end, property, pending);
            }
        }

        EndStatement(end);
    }

    private static bool IsNamePart(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '$' or '@' or '.' or '#' or '%';

    private static int SkipString(string t, int start, int end)
    {
        for (var i = start + 1; i < end; i++)
        {
            if (t[i] == '\\')
                i++;
            else if (t[i] == t[start])
                return i;
        }
        return end;
    }

    private static int ReadHex(string t, int start, int end, List<CssColorMatch> pending)
    {
        var i = start + 1;
        while (i < end && char.IsAsciiHexDigit(t[i]))
            i++;

        var digits = i - start - 1;
        var tail = i < end && (char.IsAsciiLetterOrDigit(t[i]) || t[i] is '-' or '_');
        if (!tail && digits is 3 or 4 or 6 or 8)
            pending.Add(new CssColorMatch(start, digits + 1, ParseHex(t.Substring(start + 1, digits)), CssColorFormat.Hex));
        return i - 1;
    }

    private static CssColor ParseHex(string hex)
    {
        if (hex.Length <= 4)
            hex = string.Concat(hex.Select(digit => new string(digit, 2)));
        byte Part(int index) => byte.Parse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new CssColor(Part(0), Part(2), Part(4), hex.Length == 8 ? Math.Round(Part(6) / 255d, 2) : 1);
    }

    private static int ReadName(string t, int start, int end, string property, List<CssColorMatch> pending)
    {
        var i = start;
        while (i < end && (char.IsAsciiLetterOrDigit(t[i]) || t[i] is '-' or '_'))
            i++;
        var name = t[start..i].ToLowerInvariant();

        if (i < end && t[i] == '(')
        {
            if (name == "url")
            {
                var close = t.IndexOf(')', i, end - i);
                return close < 0 ? end : close;
            }
            if (name is "rgb" or "rgba" or "hsl" or "hsla" && ReadFunction(t, start, i, end, name) is { } match)
            {
                pending.Add(match);
                return match.Start + match.Length - 1;
            }
            return i - 1;
        }

        if (NamedColors.TryGetValue(name, out var named) && !IsTextProperty(property))
            pending.Add(new CssColorMatch(start, name.Length, named, CssColorFormat.Named));
        return i - 1;
    }

    // Nessas propriedades, uma palavra como "red" é um nome qualquer, e não uma cor.
    private static bool IsTextProperty(string property) =>
        property.StartsWith("font", StringComparison.Ordinal)
        || property.StartsWith("animation", StringComparison.Ordinal)
        || property.StartsWith("grid", StringComparison.Ordinal)
        || property.StartsWith("transition", StringComparison.Ordinal)
        || property.StartsWith("counter", StringComparison.Ordinal)
        || property is "content" or "will-change";

    private static CssColorMatch? ReadFunction(string t, int start, int open, int end, string name)
    {
        var close = t.IndexOf(')', open, Math.Min(end - open, MaxFunctionLength));
        if (close < 0)
            return null;

        var parts = ArgumentSeparator().Split(t[(open + 1)..close].Trim());
        if (parts.Length is not (3 or 4))
            return null;

        var alpha = 1d;
        if (parts.Length == 4 && !TryAlpha(parts[3], out alpha))
            return null;

        CssColor color;
        CssColorFormat format;
        if (name.StartsWith("rgb", StringComparison.Ordinal))
        {
            if (!TryChannel(parts[0], out var r) || !TryChannel(parts[1], out var g) || !TryChannel(parts[2], out var b))
                return null;
            color = new CssColor(CssColor.ToByte(r), CssColor.ToByte(g), CssColor.ToByte(b), alpha);
            format = CssColorFormat.Rgb;
        }
        else
        {
            if (!TryNumber(parts[0].Replace("deg", "", StringComparison.OrdinalIgnoreCase), out var hue)
                || !TryPercent(parts[1], out var saturation)
                || !TryPercent(parts[2], out var lightness))
            {
                return null;
            }
            color = CssColor.FromHsl(hue, saturation, lightness, alpha);
            format = CssColorFormat.Hsl;
        }
        return new CssColorMatch(start, close - start + 1, color, format);
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryChannel(string text, out double unit)
    {
        var percent = text.EndsWith('%');
        var ok = TryNumber(percent ? text[..^1] : text, out var value);
        unit = Math.Clamp(percent ? value / 100 : value / 255, 0, 1);
        return ok;
    }

    private static bool TryPercent(string text, out double unit)
    {
        var ok = TryNumber(text.TrimEnd('%'), out var value);
        unit = Math.Clamp(value / 100, 0, 1);
        return ok;
    }

    private static bool TryAlpha(string text, out double alpha)
    {
        var percent = text.EndsWith('%');
        var ok = TryNumber(percent ? text[..^1] : text, out var value);
        alpha = Math.Clamp(percent ? value / 100 : value, 0, 1);
        return ok;
    }

    // Sempre com ponto: o CSS não entende a vírgula decimal do português.
    public static string Format(CssColor color, CssColorFormat format)
    {
        var alpha = color.A.ToString("0.##", CultureInfo.InvariantCulture);
        var translucent = color.A < 1;
        switch (format)
        {
            case CssColorFormat.Rgb:
                return translucent
                    ? $"rgba({color.R}, {color.G}, {color.B}, {alpha})"
                    : $"rgb({color.R}, {color.G}, {color.B})";
            case CssColorFormat.Hsl:
                var (hue, saturation, lightness) = color.ToHsl();
                var (h, s, l) = (Math.Round(hue) % 360, Math.Round(saturation * 100), Math.Round(lightness * 100));
                return translucent
                    ? $"hsla({h:0}, {s:0}%, {l:0}%, {alpha})"
                    : $"hsl({h:0}, {s:0}%, {l:0}%)";
            default:
                var hex = $"#{color.R:x2}{color.G:x2}{color.B:x2}";
                return translucent ? hex + CssColor.ToByte(color.A).ToString("x2", CultureInfo.InvariantCulture) : hex;
        }
    }

    public static CssColorFormat NextFormat(CssColorFormat format) => format switch
    {
        CssColorFormat.Hex => CssColorFormat.Rgb,
        CssColorFormat.Rgb => CssColorFormat.Hsl,
        _ => CssColorFormat.Hex,
    };

    private static readonly Dictionary<string, CssColor> NamedColors = BuildNames();

    private static Dictionary<string, CssColor> BuildNames()
    {
        const string Table =
            "aliceblue f0f8ff antiquewhite faebd7 aqua 00ffff aquamarine 7fffd4 azure f0ffff beige f5f5dc bisque ffe4c4 black 000000 "
            + "blanchedalmond ffebcd blue 0000ff blueviolet 8a2be2 brown a52a2a burlywood deb887 cadetblue 5f9ea0 chartreuse 7fff00 "
            + "chocolate d2691e coral ff7f50 cornflowerblue 6495ed cornsilk fff8dc crimson dc143c cyan 00ffff darkblue 00008b "
            + "darkcyan 008b8b darkgoldenrod b8860b darkgray a9a9a9 darkgreen 006400 darkgrey a9a9a9 darkkhaki bdb76b darkmagenta 8b008b "
            + "darkolivegreen 556b2f darkorange ff8c00 darkorchid 9932cc darkred 8b0000 darksalmon e9967a darkseagreen 8fbc8f "
            + "darkslateblue 483d8b darkslategray 2f4f4f darkslategrey 2f4f4f darkturquoise 00ced1 darkviolet 9400d3 deeppink ff1493 "
            + "deepskyblue 00bfff dimgray 696969 dimgrey 696969 dodgerblue 1e90ff firebrick b22222 floralwhite fffaf0 forestgreen 228b22 "
            + "fuchsia ff00ff gainsboro dcdcdc ghostwhite f8f8ff gold ffd700 goldenrod daa520 gray 808080 green 008000 greenyellow adff2f "
            + "grey 808080 honeydew f0fff0 hotpink ff69b4 indianred cd5c5c indigo 4b0082 ivory fffff0 khaki f0e68c lavender e6e6fa "
            + "lavenderblush fff0f5 lawngreen 7cfc00 lemonchiffon fffacd lightblue add8e6 lightcoral f08080 lightcyan e0ffff "
            + "lightgoldenrodyellow fafad2 lightgray d3d3d3 lightgreen 90ee90 lightgrey d3d3d3 lightpink ffb6c1 lightsalmon ffa07a "
            + "lightseagreen 20b2aa lightskyblue 87cefa lightslategray 778899 lightslategrey 778899 lightsteelblue b0c4de lightyellow ffffe0 "
            + "lime 00ff00 limegreen 32cd32 linen faf0e6 magenta ff00ff maroon 800000 mediumaquamarine 66cdaa mediumblue 0000cd "
            + "mediumorchid ba55d3 mediumpurple 9370db mediumseagreen 3cb371 mediumslateblue 7b68ee mediumspringgreen 00fa9a "
            + "mediumturquoise 48d1cc mediumvioletred c71585 midnightblue 191970 mintcream f5fffa mistyrose ffe4e1 moccasin ffe4b5 "
            + "navajowhite ffdead navy 000080 oldlace fdf5e6 olive 808000 olivedrab 6b8e23 orange ffa500 orangered ff4500 orchid da70d6 "
            + "palegoldenrod eee8aa palegreen 98fb98 paleturquoise afeeee palevioletred db7093 papayawhip ffefd5 peachpuff ffdab9 "
            + "peru cd853f pink ffc0cb plum dda0dd powderblue b0e0e6 purple 800080 rebeccapurple 663399 red ff0000 rosybrown bc8f8f "
            + "royalblue 4169e1 saddlebrown 8b4513 salmon fa8072 sandybrown f4a460 seagreen 2e8b57 seashell fff5ee sienna a0522d "
            + "silver c0c0c0 skyblue 87ceeb slateblue 6a5acd slategray 708090 slategrey 708090 snow fffafa springgreen 00ff7f "
            + "steelblue 4682b4 tan d2b48c teal 008080 thistle d8bfd8 tomato ff6347 turquoise 40e0d0 violet ee82ee wheat f5deb3 "
            + "white ffffff whitesmoke f5f5f5 yellow ffff00 yellowgreen 9acd32";

        var parts = Table.Split(' ');
        var names = new Dictionary<string, CssColor>(parts.Length / 2 + 1, StringComparer.Ordinal);
        for (var i = 0; i + 1 < parts.Length; i += 2)
            names[parts[i]] = ParseHex(parts[i + 1]);
        names["transparent"] = new CssColor(0, 0, 0, 0);
        return names;
    }
}
