using Avalonia;
using Avalonia.Media;

namespace Sirius;

// Linhas exibidas nas listas da janela. A lista é recriada quando algo muda.
public sealed record TreeRow(
    string Name, string FullPath, bool IsFolder, Thickness Padding, Geometry? Chevron,
    FontWeight Weight, IBrush Background, IBrush Foreground, bool IsDirty);

public sealed record TabRow(string Name, string FullPath, FontWeight Weight, IBrush Foreground, bool IsDirty);

public sealed record CommandRow(string Name, string Hint, IBrush Background, Action Run);

public sealed record ProblemRow(string Message, string Location, IBrush Brush, string FullPath, int Line, int Character);

// Na busca, cada arquivo é uma linha de título seguida das linhas com resultado.
public sealed record SearchRow(
    bool IsFile, string Title, string Hint, string FullPath, int Line, int Column, int Length,
    string Before, string Match, string After, IBrush Background)
{
    public bool IsHit => !IsFile;
}
