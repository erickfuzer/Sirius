using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

public class DropTests
{
    // O Explorer entrega itens reais do sistema de arquivos, que só o próprio Avalonia pode criar.
    private static DataTransfer FilesToDrop(MainWindow window, params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? window.StorageProvider.TryGetFolderFromPathAsync(path).GetAwaiter().GetResult()
                : window.StorageProvider.TryGetFileFromPathAsync(path).GetAwaiter().GetResult();
            data.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(item)));
        }
        return data;
    }

    private static void Send(MainWindow window, RawDragEventType type, IDataTransfer data)
    {
        window.DragDrop(new Point(300, 200), type, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Drop(MainWindow window, params string[] paths)
    {
        var data = FilesToDrop(window, paths);
        Send(window, RawDragEventType.DragEnter, data);
        Send(window, RawDragEventType.Drop, data);
    }

    private static MainWindow Open(TempFolder config, string? folder)
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(config.Path, "settings.json"), folder);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Arrastar_arquivo_mostra_o_aviso_e_soltar_abre_em_aba()
    {
        using var project = new TempFolder();
        using var config = new TempFolder();
        var file = project.File("nota.txt", "oi");
        var window = Open(config, project.Path);
        var data = FilesToDrop(window, file);

        Send(window, RawDragEventType.DragEnter, data);
        Assert.True(window.IsDropHintVisible);

        Send(window, RawDragEventType.Drop, data);

        Assert.False(window.IsDropHintVisible);
        Assert.Equal(file, window.ActiveDocument!.FullPath);
        window.Close();
    }

    [AvaloniaFact]
    public void Sair_da_janela_arrastando_esconde_o_aviso_e_nao_abre_nada()
    {
        using var project = new TempFolder();
        using var config = new TempFolder();
        var file = project.File("nota.txt", "oi");
        var window = Open(config, project.Path);
        var data = FilesToDrop(window, file);

        Send(window, RawDragEventType.DragEnter, data);
        Send(window, RawDragEventType.DragLeave, data);

        Assert.False(window.IsDropHintVisible);
        Assert.Empty(window.Documents);
        window.Close();
    }

    [AvaloniaFact]
    public void Arrastar_texto_solto_nao_e_aceito()
    {
        using var config = new TempFolder();
        var window = Open(config, null);
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText("só um texto"));

        Send(window, RawDragEventType.DragEnter, data);
        Assert.False(window.IsDropHintVisible);

        Send(window, RawDragEventType.Drop, data);
        Assert.Empty(window.Documents);
        window.Close();
    }

    [AvaloniaFact]
    public void Soltar_foto_de_fora_abre_em_aba_e_mantem_o_projeto()
    {
        using var project = new TempFolder();
        using var outside = new TempFolder();
        using var config = new TempFolder();
        var photo = outside.File("foto.bmp");
        File.WriteAllBytes(photo, TinyBmp());
        var window = Open(config, project.Path);

        Drop(window, photo);

        Assert.Equal(project.Path, window.RootFolder);
        Assert.Equal(photo, window.ActiveDocument!.FullPath);
        Assert.True(window.ActiveDocument.IsImage);
        Assert.True(window.IsImageShown);
        Assert.Equal(outside.Path, window.BreadcrumbText);
        Assert.True(File.Exists(photo));
        window.Close();
    }

    [AvaloniaFact]
    public void Soltar_varios_arquivos_abre_todos_e_ativa_o_ultimo()
    {
        using var project = new TempFolder();
        using var config = new TempFolder();
        var first = project.File("a.txt", "a");
        var second = project.File("b.txt", "b");
        var window = Open(config, project.Path);

        Drop(window, first, second);

        Assert.Equal(2, window.Documents.Count);
        Assert.Equal(second, window.ActiveDocument!.FullPath);
        window.Close();
    }

    [AvaloniaFact]
    public void Soltar_arquivo_sem_projeto_abre_a_pasta_dele()
    {
        using var project = new TempFolder();
        using var config = new TempFolder();
        var file = project.File("nota.txt", "oi");
        var window = Open(config, null);

        Drop(window, file);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(project.Path, window.RootFolder);
        Assert.Equal(file, window.ActiveDocument!.FullPath);
        window.Close();
    }

    [AvaloniaFact]
    public void Soltar_pasta_troca_o_projeto()
    {
        using var first = new TempFolder();
        using var second = new TempFolder();
        using var config = new TempFolder();
        var file = first.File("nota.txt", "oi");
        var window = Open(config, first.Path);
        window.OpenFile(file);

        Drop(window, second.Path);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(second.Path, window.RootFolder);
        Assert.Empty(window.Documents);
        window.Close();
    }

    [AvaloniaFact]
    public void Soltar_a_pasta_ja_aberta_nao_fecha_as_abas()
    {
        using var project = new TempFolder();
        using var config = new TempFolder();
        var file = project.File("nota.txt", "oi");
        var window = Open(config, project.Path);
        window.OpenFile(file);

        Drop(window, project.Path);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(window.Documents);
        window.Close();
    }

    [AvaloniaFact]
    public void Soltar_pasta_com_aba_modificada_pergunta_antes_de_trocar()
    {
        using var first = new TempFolder();
        using var second = new TempFolder();
        using var config = new TempFolder();
        var file = first.File("nota.txt", "oi");
        var window = Open(config, first.Path);
        window.OpenFile(file);
        window.ActiveDocument!.Document.Insert(0, "x");

        Drop(window, second.Path);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsDialogOpen);
        Assert.Equal(first.Path, window.RootFolder);

        window.AnswerDialog(primary: false);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(second.Path, window.RootFolder);
        Assert.Equal("oi", File.ReadAllText(file));
        window.Close();
    }

    [AvaloniaFact]
    public void Com_uma_pergunta_na_tela_soltar_nao_faz_nada()
    {
        using var first = new TempFolder();
        using var second = new TempFolder();
        using var config = new TempFolder();
        var file = first.File("nota.txt", "oi");
        var other = first.File("outra.txt", "oi");
        var window = Open(config, first.Path);
        window.OpenFile(file);
        window.ActiveDocument!.Document.Insert(0, "x");
        Drop(window, second.Path);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsDialogOpen);

        var data = FilesToDrop(window, other);
        Send(window, RawDragEventType.DragEnter, data);
        Assert.False(window.IsDropHintVisible);
        Send(window, RawDragEventType.Drop, data);

        Assert.Single(window.Documents);
        window.AnswerDialog(primary: false);
        Dispatcher.UIThread.RunJobs();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Item_que_nao_existe_mais_e_ignorado()
    {
        using var project = new TempFolder();
        using var config = new TempFolder();
        var window = Open(config, project.Path);

        await window.OpenDroppedAsync([project.File("sumiu.txt")]);

        Assert.Empty(window.Documents);
        Assert.Equal(project.Path, window.RootFolder);
        window.Close();
    }

    private static byte[] TinyBmp()
    {
        var bytes = new byte[58];
        bytes[0] = 0x42;
        bytes[1] = 0x4D;
        bytes[10] = 54;
        bytes[14] = 40;
        bytes[18] = 1;
        bytes[22] = 1;
        bytes[26] = 1;
        bytes[28] = 24;
        return bytes;
    }
}
