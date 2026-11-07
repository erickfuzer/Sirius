using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Sirius;

// Arrastar do Explorer para a janela: pasta vira projeto, arquivo vira aba.
public partial class MainWindow
{
    public bool IsDropHintVisible => DropOverlay.IsVisible;
    public string BreadcrumbText => Breadcrumb.Text ?? "";

    private void InitializeDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOverWindow);
        AddHandler(DragDrop.DragOverEvent, OnDragOverWindow);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeaveWindow);
        AddHandler(DragDrop.DropEvent, OnDropOnWindow);
    }

    private void OnDragOverWindow(object? sender, DragEventArgs e)
    {
        var accepts = !IsDialogOpen && e.DataTransfer.Contains(DataFormat.File);
        // Sempre cópia: com "mover", o Explorer apagaria o arquivo original depois de soltar.
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        ShowDropHint(accepts);
        e.Handled = true;
    }

    private void OnDragLeaveWindow(object? sender, DragEventArgs e) => ShowDropHint(false);

    // Só a opacidade do texto de "nenhuma aba": quem decide se ele aparece é o resto da janela.
    private void ShowDropHint(bool show)
    {
        DropOverlay.IsVisible = show;
        EmptyState.Opacity = show ? 0 : 1;
    }

    private void OnDropOnWindow(object? sender, DragEventArgs e)
    {
        ShowDropHint(false);
        e.Handled = true;
        var paths = IsDialogOpen ? [] : e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        e.DragEffects = paths.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        if (paths.Count == 0)
            return;

        // O Explorer fica parado até esta função voltar, então abrir fica para depois.
        Dispatcher.UIThread.Post(() => _ = OpenDroppedAsync(paths));
    }

    // Arquivos que outra Sirius, aberta pelo "Abrir com Sirius", entregou a esta janela. Só arquivos que existem:
    // o caminho veio de outro programa.
    public void ReceiveForwarded(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
            return;
        BringToFrontForPause();
        _ = OpenDroppedAsync(files);
    }

    public async Task OpenDroppedAsync(IReadOnlyList<string> paths)
    {
        ClosePalette();
        base.Activate();

        if (paths.FirstOrDefault(Directory.Exists) is { } folder)
        {
            if (!string.Equals(Path.GetFullPath(folder), RootFolder, StringComparison.OrdinalIgnoreCase))
                await SwitchFolderAsync(folder);
            return;
        }

        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
            return;

        // Com um projeto aberto, o arquivo de fora só ganha uma aba e o projeto continua.
        if (RootFolder is null)
        {
            if (!await ConfirmStopTaskAsync())
                return;
            OpenFolder(Path.GetDirectoryName(Path.GetFullPath(files[0]))!);
        }

        foreach (var file in files)
            OpenFile(file);
    }
}
