using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using Sirius.Core;

namespace Sirius.Lsp;

public enum DiagnosticSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }

public sealed record LspDiagnostic(int Line, int Character, int EndLine, int EndCharacter, DiagnosticSeverity Severity, string Message);

public sealed record CompletionEntry(string Label, string? Detail, string? Documentation, string InsertText, string? SortText)
{
    // O trecho que o servidor mandou substituir. Sem isto, valeria a palavra sob o cursor,
    // e "background-co" viraria "background-background-color", porque o hífen separa palavras.
    public LspRange? Edit { get; init; }

    // Onde o cursor fica dentro do texto inserido, como entre as aspas de class="". Sem isto, no fim.
    public int? CaretInInsertText { get; init; }

    // Mais de um campo: ao aceitar, o Tab anda entre eles.
    public SnippetText? Snippet { get; init; }

    // Abrir a lista de novo ao escolher, como os valores depois de "color: ".
    public bool SuggestAgain { get; init; }
}

public readonly record struct LspRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

public sealed record LspLocation(string Path, int Line, int Character);

// Um uso de um nome, com linha e caractere a partir de zero, como o servidor manda.
public sealed record LspReference(string Path, int Line, int Character, int EndLine, int EndCharacter);

public sealed record LspTextEdit(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string NewText);

// Lê as respostas do servidor, que aceitam vários formatos pela especificação.
public static partial class LspParsers
{
    public static string ToUri(string path) => new Uri(path).AbsoluteUri;

    public static string ToPath(string uri)
    {
        var parsed = new Uri(uri);
        if (!string.IsNullOrEmpty(parsed.Host))
            return parsed.LocalPath;

        // Alguns servidores, como o Intelephense, escrevem file:///c%3A/pasta, e o Uri do
        // .NET não reconhece esse ":" codificado como letra de unidade.
        var path = Uri.UnescapeDataString(parsed.AbsolutePath);
        if (path.Length >= 3 && path[0] == '/' && path[2] == ':')
            path = path[1..];
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            path = char.ToUpperInvariant(path[0]) + path[1..];
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    public static List<LspDiagnostic> Diagnostics(JsonElement parameters)
    {
        var list = new List<LspDiagnostic>();
        if (!parameters.TryGetProperty("diagnostics", out var items) || items.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in items.EnumerateArray())
        {
            if (!TryRange(item, "range", out var range))
                continue;
            var severity = item.TryGetProperty("severity", out var value) && value.TryGetInt32(out var number) && number is >= 1 and <= 4
                ? (DiagnosticSeverity)number
                : DiagnosticSeverity.Error;
            var message = item.TryGetProperty("message", out var text) ? text.GetString() ?? "" : "";
            list.Add(new LspDiagnostic(range.StartLine, range.StartCharacter, range.EndLine, range.EndCharacter, severity, message));
        }
        return list;
    }

    public static List<CompletionEntry> Completion(JsonElement result)
    {
        var entries = new List<CompletionEntry>();
        var items = result;
        if (result.ValueKind == JsonValueKind.Object && !result.TryGetProperty("items", out items))
            return entries;
        if (items.ValueKind != JsonValueKind.Array)
            return entries;

        foreach (var item in items.EnumerateArray())
        {
            var label = StringOf(item, "label");
            if (string.IsNullOrEmpty(label))
                continue;

            var insert = StringOf(item, "insertText") ?? label;
            LspRange? range = null;
            if (item.TryGetProperty("textEdit", out var edit))
            {
                insert = StringOf(edit, "newText") ?? insert;
                if (TryRange(edit, "range", out var textRange))
                    range = new LspRange(textRange.StartLine, textRange.StartCharacter, textRange.EndLine, textRange.EndCharacter);
            }

            // Um trecho com campos, como class="$1": lido pelo mesmo leitor dos atalhos e do Emmet.
            int? caret = null;
            SnippetText? snippet = null;
            if (item.TryGetProperty("insertTextFormat", out var format) && format.TryGetInt32(out var kind) && kind == 2)
            {
                snippet = Snippet.Parse(insert);
                insert = snippet.Text;
                caret = FirstStop(snippet);
            }

            var again = item.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.Object
                && StringOf(command, "command") == "editor.action.triggerSuggest";

            var documentation = item.TryGetProperty("documentation", out var docs) ? CleanDocumentation(TextOf(docs)) : null;
            entries.Add(new CompletionEntry(label, StringOf(item, "detail"), documentation, insert, StringOf(item, "sortText"))
            {
                Edit = range,
                CaretInInsertText = caret,
                Snippet = snippet is { Stops.Count: > 1 } ? snippet : null,
                SuggestAgain = again,
            });
        }
        return entries;
    }

    // Onde o cursor fica sem navegar entre os campos: no primeiro, ou no $0 se só houver ele.
    public static int? FirstStop(SnippetText snippet)
    {
        var first = snippet.Order.FirstOrDefault(-1);
        return first < 0 ? null : snippet.Stops.First(stop => stop.Index == first).Offset;
    }

    public static LspLocation? Definition(JsonElement result)
    {
        var first = result;
        if (result.ValueKind == JsonValueKind.Array)
        {
            if (result.GetArrayLength() == 0)
                return null;
            first = result[0];
        }
        if (first.ValueKind != JsonValueKind.Object)
            return null;

        if (first.TryGetProperty("targetUri", out var target) && TryRange(first, "targetSelectionRange", out var selection))
            return new LspLocation(ToPath(target.GetString()!), selection.StartLine, selection.StartCharacter);
        if (first.TryGetProperty("uri", out var uri) && TryRange(first, "range", out var range))
            return new LspLocation(ToPath(uri.GetString()!), range.StartLine, range.StartCharacter);
        return null;
    }

    // A resposta de "references": uma lista de lugares, cada um com o trecho que é o nome.
    public static List<LspReference> References(JsonElement result)
    {
        var references = new List<LspReference>();
        if (result.ValueKind != JsonValueKind.Array)
            return references;
        foreach (var item in result.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("uri", out var uri) && uri.GetString() is { } text
                && TryRange(item, "range", out var range))
            {
                references.Add(new LspReference(ToPath(text), range.StartLine, range.StartCharacter, range.EndLine, range.EndCharacter));
            }
        }
        return references;
    }

    public static string? Hover(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents))
            return null;
        var text = CleanDocumentation(TextOf(contents));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static Dictionary<string, List<LspTextEdit>> WorkspaceEdit(JsonElement result)
    {
        var edits = new Dictionary<string, List<LspTextEdit>>(StringComparer.OrdinalIgnoreCase);
        if (result.ValueKind != JsonValueKind.Object)
            return edits;

        if (result.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var change in changes.EnumerateObject())
                edits[ToPath(change.Name)] = TextEdits(change.Value);
        }
        if (result.TryGetProperty("documentChanges", out var documents) && documents.ValueKind == JsonValueKind.Array)
        {
            foreach (var document in documents.EnumerateArray())
            {
                if (document.TryGetProperty("textDocument", out var target) && target.TryGetProperty("uri", out var uri)
                    && document.TryGetProperty("edits", out var list))
                    edits[ToPath(uri.GetString()!)] = TextEdits(list);
            }
        }
        return edits;
    }

    // A resposta de formatar: uma lista de trocas, ou null quando não há nada a mudar.
    public static List<LspTextEdit> TextEditList(JsonElement result) =>
        result.ValueKind == JsonValueKind.Array ? TextEdits(result) : [];

    private static List<LspTextEdit> TextEdits(JsonElement list)
    {
        var edits = new List<LspTextEdit>();
        foreach (var item in list.EnumerateArray())
        {
            if (TryRange(item, "range", out var range))
                edits.Add(new LspTextEdit(range.StartLine, range.StartCharacter, range.EndLine, range.EndCharacter, StringOf(item, "newText") ?? ""));
        }
        return edits;
    }

    // A Sirius mostra texto puro. O servidor de CSS manda até o ícone em base64 no meio da
    // descrição, que na tela vira uma linha enorme de letras sem sentido.
    public static string? CleanDocumentation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = MarkdownImage().Replace(text, "");
        text = MarkdownLink().Replace(text, "$1");
        text = text.Replace("\\\n", "\n").Replace("\\\r\n", "\n");
        text = WebUtility.HtmlDecode(text);
        text = ExtraBlankLines().Replace(text, "\n\n").Trim();
        if (text.Length == 0)
            return null;
        return text.Length <= MaxDocumentationLength ? text : text[..MaxDocumentationLength].TrimEnd() + "…";
    }

    private const int MaxDocumentationLength = 1500;

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(\r?\n[ \t]*){3,}")]
    private static partial Regex ExtraBlankLines();

    private static string? TextOf(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Object:
                return StringOf(value, "value");
            case JsonValueKind.Array:
                return string.Join("\n\n", value.EnumerateArray().Select(TextOf).Where(t => !string.IsNullOrEmpty(t)));
            default:
                return null;
        }
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private readonly record struct RangeValue(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

    private static bool TryRange(JsonElement element, string name, out RangeValue range)
    {
        range = default;
        if (!element.TryGetProperty(name, out var value)
            || !value.TryGetProperty("start", out var start) || !value.TryGetProperty("end", out var end))
            return false;
        range = new RangeValue(
            start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32(),
            end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32());
        return true;
    }
}

// Converte entre as posições do servidor (linha e caractere a partir de zero) e o documento.
public static class LspPositions
{
    public static int ToOffset(TextDocument document, int line, int character)
    {
        if (line >= document.LineCount)
            return document.TextLength;
        var target = document.GetLineByNumber(Math.Max(line, 0) + 1);
        return target.Offset + Math.Clamp(character, 0, target.Length);
    }

    public static (int Line, int Character) FromOffset(TextDocument document, int offset)
    {
        var line = document.GetLineByOffset(offset);
        return (line.LineNumber - 1, offset - line.Offset);
    }

    // Aplica de trás para frente, para uma edição não deslocar a posição da próxima.
    public static void ApplyEdits(TextDocument document, IEnumerable<LspTextEdit> edits)
    {
        var ordered = edits
            .Select(edit => (Start: ToOffset(document, edit.StartLine, edit.StartCharacter), End: ToOffset(document, edit.EndLine, edit.EndCharacter), edit.NewText))
            .OrderByDescending(edit => edit.Start)
            .ToList();

        using (document.RunUpdate())
        {
            foreach (var edit in ordered)
                document.Replace(edit.Start, edit.End - edit.Start, edit.NewText);
        }
    }
}
