using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// Drag-and-drop import over the whole window (official design §6.25). Entering shows the zones for what is being
/// dragged, moving highlights the zone under the pointer, dropping on a zone runs it, leaving hides everything.
/// </summary>
public partial class MainWindow
{
    private void AttachDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Compact windows stack the zones (each takes the width).
        SizeChanged += (_, e) => { if (DropHost.IsBuilt) DropLayer.Classes.Set("compact", e.NewSize.Width < 640); };
    }

    private DropViewModel? DropModel => (DataContext as MainViewModel)?.Drop;

    private static List<string> FilesOf(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (DropModel is not { } drop) return;
        var files = FilesOf(e);
        if (files.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        drop.Begin(files);
        e.DragEffects = DragDropEffects.Copy;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (DropModel is not { IsActive: true } drop) return;
        var target = TargetAt(e.GetPosition(this));
        drop.Hover(target);
        e.DragEffects = target is null ? DragDropEffects.None : DragDropEffects.Copy;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // Leave also fires when moving between children: only end when the pointer really left the window.
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= Bounds.Width || p.Y >= Bounds.Height) DropModel?.End();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DropModel is not { } drop) return;
        var target = TargetAt(e.GetPosition(this));
        await drop.DropAsync(target);
    }

    /// <summary>The zone under a point of the window, if any.</summary>
    internal DropTarget? TargetAt(Point point)
    {
        foreach (var border in DropTargets.GetVisualDescendants().OfType<Border>())
        {
            if (border.DataContext is not DropTarget target || !border.Classes.Contains("drop-target")) continue;
            if (border.TranslatePoint(new Point(0, 0), this) is { } origin && new Rect(origin, border.Bounds.Size).Contains(point)) return target;
        }
        return null;
    }
}
