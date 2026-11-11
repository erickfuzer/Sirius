using System.Text;
using System.Text.RegularExpressions;

namespace Sirius.Core;

// Procurar e substituir dentro do arquivo aberto, como a caixa do VS Code (Ctrl F, F3).
public sealed record FindOptions(string Text, bool MatchCase = false, bool WholeWord = false, bool UseRegex = false);

public readonly record struct FindMatch(int Offset, int Length)
{
    public int End => Offset + Length;
}

// Invalid: a expressão regular não compila. Truncated: parou no limite ou no tempo.
public sealed record FindResult(IReadOnlyList<FindMatch> Matches, bool Invalid, bool Truncated)
{
    public static readonly FindResult Empty = new([], false, false);
}

public static class FindInText
{
    // O VS Code também para de contar perto de 20 mil.
    public const int Limit = 19999;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    // Tudo vira expressão regular: o texto comum é escapado, e "palavra inteira" cerca com o que não é letra,
    // número ou _. O $ do PHP fica de fora da palavra, então "nome" acha o $nome, como no VS Code.
    public static Regex? BuildRegex(FindOptions options)
    {
        if (options.Text.Length == 0)
            return null;
        var pattern = options.UseRegex ? options.Text : Regex.Escape(options.Text);
        if (options.WholeWord)
            pattern = $@"(?<!\w)(?:{pattern})(?!\w)";
        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(pattern, flags, Timeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static FindResult FindAll(string text, FindOptions options, int limit = Limit)
    {
        if (options.Text.Length == 0)
            return FindResult.Empty;
        if (BuildRegex(options) is not { } regex)
            return new([], true, false);

        var matches = new List<FindMatch>();
        try
        {
            for (var match = regex.Match(text); match.Success; match = match.NextMatch())
            {
                // Um resultado vazio, como o ^ sozinho, não tem o que marcar nem trocar.
                if (match.Length == 0)
                    continue;
                if (matches.Count == limit)
                    return new(matches, false, true);
                matches.Add(new(match.Index, match.Length));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new(matches, false, true);
        }
        return new(matches, false, false);
    }

    // O texto que entra no lugar de um resultado. Com expressão regular, $1 traz o grupo e \n quebra a linha.
    public static string ReplacementFor(string text, FindMatch match, FindOptions options, string replacement)
    {
        if (!options.UseRegex || BuildRegex(options) is not { } regex)
            return replacement;
        try
        {
            var found = regex.Match(text, match.Offset);
            if (!found.Success || found.Index != match.Offset || found.Length != match.Length)
                return replacement;
            return found.Result(Unescape(replacement, NewLineOf(text)));
        }
        catch (RegexMatchTimeoutException)
        {
            return replacement;
        }
    }

    // Todas as trocas, do fim para o começo, para os endereços não andarem enquanto se troca.
    public static List<(FindMatch Match, string Text)> ReplaceAllPlan(string text, FindOptions options, string replacement)
    {
        var plan = new List<(FindMatch, string)>();
        if (BuildRegex(options) is not { } regex)
            return plan;
        var expanded = options.UseRegex ? Unescape(replacement, NewLineOf(text)) : replacement;
        try
        {
            for (var match = regex.Match(text); match.Success; match = match.NextMatch())
            {
                if (match.Length == 0)
                    continue;
                plan.Add((new(match.Index, match.Length), options.UseRegex ? match.Result(expanded) : replacement));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
        plan.Reverse();
        return plan;
    }

    // O primeiro resultado que começa em offset ou depois; sem nenhum, volta ao primeiro do arquivo.
    public static int IndexFrom(IReadOnlyList<FindMatch> matches, int offset)
    {
        if (matches.Count == 0)
            return -1;
        var index = FirstAtOrAfter(matches, offset);
        return index < matches.Count ? index : 0;
    }

    // O último resultado que começa antes de offset; sem nenhum, vai ao último do arquivo.
    public static int IndexBefore(IReadOnlyList<FindMatch> matches, int offset)
    {
        if (matches.Count == 0)
            return -1;
        var index = FirstAtOrAfter(matches, offset) - 1;
        return index >= 0 ? index : matches.Count - 1;
    }

    // A posição do resultado que é exatamente este trecho, ou -1.
    public static int IndexOf(IReadOnlyList<FindMatch> matches, int offset, int length)
    {
        var index = FirstAtOrAfter(matches, offset);
        return index < matches.Count && matches[index].Offset == offset && matches[index].Length == length ? index : -1;
    }

    public static int FirstAtOrAfter(IReadOnlyList<FindMatch> matches, int offset)
    {
        int low = 0, high = matches.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (matches[middle].Offset < offset)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static string NewLineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    // Como no VS Code: \n e \t no texto de troca, e \\ para a própria barra.
    private static string Unescape(string replacement, string newLine)
    {
        if (!replacement.Contains('\\'))
            return replacement;
        var result = new StringBuilder(replacement.Length);
        for (var i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] == '\\' && i + 1 < replacement.Length)
            {
                var next = replacement[i + 1];
                var escaped = next switch { 'n' => newLine, 't' => "\t", '\\' => "\\", _ => null };
                if (escaped is not null)
                {
                    result.Append(escaped);
                    i++;
                    continue;
                }
            }
            result.Append(replacement[i]);
        }
        return result.ToString();
    }
}
