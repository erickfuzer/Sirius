using Microsoft.VisualBasic.FileIO;

namespace Sirius.Core;

// Criar e excluir arquivos e pastas. Criar nunca sobrescreve, e excluir vai para a Lixeira.
public static class FileOperations
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Devolve o motivo de o nome não servir, ou null quando ele é válido.
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Digite um nome.";
        if (name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || name.Any(char.IsControl))
            return "O nome não pode ter < > : \" / \\ | ? *";
        if (name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
            return "O nome não pode começar com espaço nem terminar com ponto ou espaço.";
        if (ReservedNames.Contains(name.Split('.')[0]))
            return "Este nome é reservado pelo Windows.";
        return null;
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static string CreateFile(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (Directory.Exists(path))
            throw new IOException("Já existe uma pasta com este nome.");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        return path;
    }

    public static string CreateFolder(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (Exists(path))
            throw new IOException("Já existe um item com este nome.");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Rename(string path, string newName)
    {
        var target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        var onlyCaseChanged = string.Equals(path, target, StringComparison.OrdinalIgnoreCase);
        if (!onlyCaseChanged && Exists(target))
            throw new IOException("Já existe um item com este nome.");

        if (!Directory.Exists(path))
        {
            File.Move(path, target);
        }
        else if (onlyCaseChanged)
        {
            // O Windows não troca só maiúsculas de uma pasta de uma vez: passa por um nome temporário.
            var temporary = target + ".sirius-tmp";
            Directory.Move(path, temporary);
            Directory.Move(temporary, target);
        }
        else
        {
            Directory.Move(path, target);
        }
        return target;
    }

    public static void MoveToTrash(string path)
    {
        if (Directory.Exists(path))
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        else
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }

    public static bool IsInside(string path, string folderOrFile) =>
        string.Equals(path, folderOrFile, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folderOrFile.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
