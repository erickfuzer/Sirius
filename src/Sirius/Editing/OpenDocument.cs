using System.Text;
using AvaloniaEdit.Document;
using Sirius.Core;

namespace Sirius.Editing;

// Um arquivo aberto em uma aba, com o histórico de desfazer e a posição de leitura.
public sealed class OpenDocument
{
    private DateTime? _acknowledgedWriteUtc;

    private OpenDocument(string path, LoadedText loaded, bool untitled)
    {
        FullPath = path;
        IsUntitled = untitled;
        Encoding = loaded.Encoding;
        LastWriteUtc = loaded.LastWriteUtc;
        Document = new TextDocument(loaded.Text);
        Document.UndoStack.MarkAsOriginalFile();
        Highlighter = SyntaxHighlighter.ForDocument(path, Document);
    }

    public SyntaxHighlighter? Highlighter { get; private set; }
    public string FullPath { get; private set; }
    public string Name => Path.GetFileName(FullPath);
    public bool IsUntitled { get; private set; }
    public bool IsImage { get; private init; }
    public Encoding Encoding { get; private set; }
    public TextDocument Document { get; }
    public DateTime LastWriteUtc { get; private set; }
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public bool IsDirty => !Document.UndoStack.IsOriginalFile;
    public bool MissingReported { get; set; }

    // Âncoras, e não números: o ponto acompanha a linha quando se escreve acima dela.
    private readonly List<TextAnchor> _breakpoints = new();

    public IReadOnlyList<int> BreakpointLines =>
        _breakpoints.Where(anchor => !anchor.IsDeleted).Select(anchor => anchor.Line).Distinct().Order().ToList();

    public void SetBreakpoints(IEnumerable<int> lines)
    {
        _breakpoints.Clear();
        foreach (var line in lines.Where(line => line >= 1 && line <= Document.LineCount))
            AddBreakpoint(line);
    }

    // Devolve se a linha ficou marcada.
    public bool ToggleBreakpoint(int line)
    {
        if (BreakpointLines.Contains(line))
        {
            _breakpoints.RemoveAll(anchor => anchor.IsDeleted || anchor.Line == line);
            return false;
        }
        AddBreakpoint(line);
        return true;
    }

    private void AddBreakpoint(int line)
    {
        var anchor = Document.CreateAnchor(Document.GetLineByNumber(line).Offset);
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        _breakpoints.Add(anchor);
    }

    public bool ChangedOnDisk =>
        !IsUntitled && File.Exists(FullPath) && File.GetLastWriteTimeUtc(FullPath) != LastWriteUtc;

    public bool MissingOnDisk => !IsUntitled && !File.Exists(FullPath);

    // Mudou no disco e a pessoa ainda não decidiu o que fazer com esta versão.
    public bool NeedsDiskAttention =>
        ChangedOnDisk && File.GetLastWriteTimeUtc(FullPath) != _acknowledgedWriteUtc;

    public static OpenDocument Load(string path) => new(path, TextFile.Load(path), false);

    // A imagem é desenhada à parte. O documento fica vazio e nunca é gravado por cima do arquivo.
    public static OpenDocument LoadImage(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(null, path);
        return new OpenDocument(path, new LoadedText("", new UTF8Encoding(false), File.GetLastWriteTimeUtc(path)), false) { IsImage = true };
    }

    // Uma aba nova. O caminho é só um nome provisório até a pessoa salvar.
    public static OpenDocument CreateUntitled(string folder, string name) =>
        new(Path.Combine(folder, name), new LoadedText("", new UTF8Encoding(false), default), true);

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
        if (IsImage)
            return;
        LastWriteUtc = TextFile.Save(FullPath, Document.Text, Encoding);
        Document.UndoStack.MarkAsOriginalFile();
    }

    // Grava em outro lugar e passa a apontar para ele. Se a gravação falhar, nada muda.
    public void SaveTo(string path)
    {
        if (IsImage)
            return;
        LastWriteUtc = TextFile.Save(path, Document.Text, Encoding);
        IsUntitled = false;
        MoveTo(path);
        Document.UndoStack.MarkAsOriginalFile();
    }

    public void AcknowledgeDiskVersion() => _acknowledgedWriteUtc = File.GetLastWriteTimeUtc(FullPath);

    public void ReloadFromDisk()
    {
        if (IsImage)
        {
            LastWriteUtc = File.GetLastWriteTimeUtc(FullPath);
            return;
        }

        var loaded = TextFile.Load(FullPath);
        SetTextKeepingPosition(loaded.Text);
        Encoding = loaded.Encoding;
        LastWriteUtc = loaded.LastWriteUtc;
        Document.UndoStack.MarkAsOriginalFile();
    }

    // Troca só o trecho que mudou, para o cursor e a rolagem continuarem onde estavam.
    public void SetTextKeepingPosition(string text)
    {
        var current = Document.Text;
        if (current == text)
            return;

        var prefix = 0;
        var limit = Math.Min(current.Length, text.Length);
        while (prefix < limit && current[prefix] == text[prefix])
            prefix++;

        var suffix = 0;
        limit -= prefix;
        while (suffix < limit && current[current.Length - 1 - suffix] == text[text.Length - 1 - suffix])
            suffix++;

        Document.Replace(prefix, current.Length - prefix - suffix, text.Substring(prefix, text.Length - prefix - suffix));
    }
}
