using AvaloniaEdit;

namespace Sirius.Editing;

// Um lado do editor dividido. Cada lado tem o seu editor, a sua margem (pontos de parada, marcas do Git e o
// fio), as suas cores e os quadradinhos de cor do CSS, e mostra um documento. O lado com o foco é "o" editor:
// sugestões, Emmet, busca e salvar valem para ele.
public sealed class EditorPane
{
    public EditorPane(TextEditor editor, string name)
    {
        Editor = editor;
        Name = name;
        Gutter = new GutterMargin(editor.TextArea);
    }

    public TextEditor Editor { get; }
    public string Name { get; }
    public GutterMargin Gutter { get; }
    public TypographicColorizer Colorizer { get; } = new();
    public CssColorGenerator Swatches { get; set; } = null!;
    public OpenDocument? Document { get; set; }

    public override string ToString() => Name;
}
