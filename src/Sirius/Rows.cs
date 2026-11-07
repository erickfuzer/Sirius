using Avalonia;
using Avalonia.Media;

namespace Sirius;

// Linhas exibidas nas listas da janela. A lista é recriada quando algo muda.
public sealed record TreeRow(
    string Name, string FullPath, bool IsFolder, Thickness Padding, Geometry? Chevron,
    FontWeight Weight, IBrush Background, IBrush Foreground, bool IsDirty, string GitLetter, IBrush GitBrush);

public sealed record TabRow(string Name, string FullPath, FontWeight Weight, IBrush Foreground, bool IsDirty);

public sealed record CommandRow(string Name, string Hint, IBrush Background, Action Run);

public sealed record RecentRow(string Name, string Folder, string FullPath);

public sealed record GitRow(string Name, string Folder, string Letter, IBrush Brush, bool IsIncluded, Sirius.Core.GitChange Change);

// O histórico mistura quatro tipos de linha: commit, arquivo dele, aviso e "carregar mais".
public sealed record HistoryCommitRow(Sirius.Core.GitCommit Commit, string Detail, string Tip, Geometry Chevron);

public sealed record HistoryFileRow(
    Sirius.Core.GitCommit Commit, Sirius.Core.GitChange Change, string Name, string Folder, string Letter, IBrush Brush, IBrush Background);

public sealed record HistoryNoteRow(string Text);

public sealed record HistoryMoreRow(string Text);

public sealed record VariableRow(string Name, string Value, string Type, Thickness Indent, Geometry? Chevron, IBrush Background, Sirius.Core.DebugVariable Variable);

public sealed record ProblemRow(string Message, string Location, IBrush Brush, string FullPath, int Line, int Character);

// Na busca, cada arquivo é uma linha de título seguida das linhas com resultado.
public sealed record SearchRow(
    bool IsFile, string Title, string Hint, string FullPath, int Line, int Column, int Length,
    string Before, string Match, string After, IBrush Background)
{
    public bool IsHit => !IsFile;
}
