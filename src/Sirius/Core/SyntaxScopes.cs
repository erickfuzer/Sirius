namespace Sirius.Core;

public enum TokenKind { Keyword, Type, Function, String, Number, Comment, Punctuation }

public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind);

// Traduz os escopos das gramáticas TextMate para as cores da sintaxe.
public static class SyntaxScopes
{
    public static TokenKind? Classify(IReadOnlyList<string> scopes, char firstChar = '\0')
    {
        // O escopo mais específico vem por último, então a leitura começa pelo fim.
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (Is(scope, "punctuation.definition.comment") || Is(scope, "punctuation.definition.string"))
                continue;
            if (Is(scope, "meta.interpolation") || Is(scope, "meta.embedded"))
                return null;
            if (Is(scope, "comment"))
                return TokenKind.Comment;
            if (Is(scope, "string") || Is(scope, "constant.character.escape") || Is(scope, "markup.inline.raw"))
                return TokenKind.String;
            if (Is(scope, "constant.numeric") || Is(scope, "constant.character"))
                return TokenKind.Number;
            // Operadores escritos como palavra, como new e typeof, contam como palavra-chave.
            if (Is(scope, "keyword.operator"))
                return char.IsLetter(firstChar) ? TokenKind.Keyword : TokenKind.Punctuation;
            if (Is(scope, "keyword") || Is(scope, "storage") || Is(scope, "constant.language") || Is(scope, "markup.heading"))
                return TokenKind.Keyword;
            // Variáveis ficam na cor normal do texto, para o código não virar um arco-íris.
            if (Is(scope, "entity.name.variable"))
                return null;
            if (Is(scope, "entity.name.function") || Is(scope, "support.function") || Is(scope, "entity.other.attribute-name"))
                return TokenKind.Function;
            if (Is(scope, "entity") || Is(scope, "support.class") || Is(scope, "support.type"))
                return TokenKind.Type;
            if (Is(scope, "punctuation"))
                return TokenKind.Punctuation;
        }
        return null;
    }

    private static bool Is(string scope, string prefix) =>
        scope.StartsWith(prefix, StringComparison.Ordinal)
        && (scope.Length == prefix.Length || scope[prefix.Length] == '.');
}
