using System.Text;
using AvaloniaEdit.Document;
using Sirius.Core;

namespace Sirius.Editing;

// Um arquivo aberto em uma aba, com o histórico de desfazer e a posição de leitura.
public sealed class OpenDocument
{
    private OpenDocument(string path, LoadedText loaded)
    {
        FullPath = path;
        Encoding = loaded.Encoding;
        LastWriteUtc = loaded.LastWriteUtc;
        Document = new TextDocument(loaded.Text);
        Document.UndoStack.MarkAsOriginalFile();
        Highlighter = SyntaxHighlighter.ForDocument(path, Document);
    }

    public SyntaxHighlighter? Highlighter { get; private set; }
    public string FullPath { get; private set; }
    public string Name => Path.GetFileName(FullPath);
    public Encoding Encoding { get; }
    public TextDocument Document { get; }
    public DateTime LastWriteUtc { get; private set; }
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public bool IsDirty => !Document.UndoStack.IsOriginalFile;

    public bool ChangedOnDisk =>
        File.Exists(FullPath) && File.GetLastWriteTimeUtc(FullPath) != LastWriteUtc;

    public static OpenDocument Load(string path) => new(path, TextFile.Load(path));

    // O arquivo mudou de nome ou de pasta. Uma extensão nova pode trazer outra linguagem.
    public void MoveTo(string path)
    {
        var languageChanged = !string.Equals(Path.GetExtension(path), Path.GetExtension(FullPath), StringComparison.OrdinalIgnoreCase);
        FullPath = path;
        if (languageChanged)
        {
            Highlighter?.Detach();
            Highlighter = SyntaxHighlighter.ForDocument(path, Document);
        }
    }

    public void Save()
    {
        LastWriteUtc = TextFile.Save(FullPath, Document.Text, Encoding);
        Document.UndoStack.MarkAsOriginalFile();
    }
}
