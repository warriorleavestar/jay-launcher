using Amber.Core.Localization;
using Amber.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Amber.App.Views;

public partial class MultiplayerPage : UserControl
{
    /// <summary>主题包可以重新排这一页的区块（docs/themes.md 的“页面布局”）。</summary>
    internal Amber.App.Services.ThemeLayoutHost ThemeLayout { get; }

    public MultiplayerPage()
    {
        InitializeComponent();
        ThemeLayout = Amber.App.Services.ThemeLayoutHost.Attach(this, "multiplayer");
        SizeChanged += (_, e) => ApplyLobbyLayout(e.NewSize.Width);
        DataContextChanged += (_, _) =>
        {
            // 新消息来了滚到底。
            if (DataContext is MultiplayerViewModel vm)
            {
                vm.PickFileToSend = async () =>
                {
                    if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
                    var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T("multiplayer.private.pick_title") });
                    return files.Count > 0 ? files[0].TryGetLocalPath() : null;
                };
                vm.ChatLines.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("ChatScroll")?.ScrollToEnd(), DispatcherPriority.Background);
                vm.DebugLines.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("DebugScroll")?.ScrollToEnd(), DispatcherPriority.Background);
                // 开服控制台：新日志来了把光标放到最后，文本框就滚到最新一行。
                vm.HostServer.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(HostServerViewModel.LogText)) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (this.FindControl<TextBox>("HostServerLog") is { Text: { } text } box) box.CaretIndex = text.Length;
                    }, DispatcherPriority.Background);
                };
            }
        };
    }

    /// <summary>Lobby cards at the page's width: side by side when there is room, else stacked (compact: join first).</summary>
    internal LobbyArrangement LobbyLayout { get; private set; } = LobbyArrangement.SideBySide;

    internal enum LobbyArrangement { SideBySide, Stacked, JoinFirst }

    private void ApplyLobbyLayout(double width)
    {
        var layout = width >= 820 ? LobbyArrangement.SideBySide : width >= 560 ? LobbyArrangement.Stacked : LobbyArrangement.JoinFirst;
        LobbyLayout = layout;
        var grid = LobbyGrid;
        if (layout == LobbyArrangement.SideBySide)
        {
            grid.ColumnDefinitions = new ColumnDefinitions("*,14,*");
            Place(HostCard, 0, 0, 1);
            Place(JoinCard, 0, 2, 1);
        }
        else
        {
            grid.ColumnDefinitions = new ColumnDefinitions("*");
            var hostFirst = layout == LobbyArrangement.Stacked;
            Place(HostCard, hostFirst ? 0 : 1, 0, 1);
            Place(JoinCard, hostFirst ? 1 : 0, 0, 1);
            HostCard.Margin = JoinCard.Margin = new Avalonia.Thickness(0);
            (hostFirst ? JoinCard : HostCard).Margin = new Avalonia.Thickness(0, 14, 0, 0);
        }
        if (layout == LobbyArrangement.SideBySide) HostCard.Margin = JoinCard.Margin = new Avalonia.Thickness(0);
        Grid.SetColumnSpan(LobbyFooter, layout == LobbyArrangement.SideBySide ? 3 : 1);
        HostCard.MaxWidth = JoinCard.MaxWidth = layout == LobbyArrangement.Stacked ? 720 : double.PositiveInfinity;
        HostCard.HorizontalAlignment = JoinCard.HorizontalAlignment = layout == LobbyArrangement.Stacked ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Stretch;
        if (layout == LobbyArrangement.Stacked) HostCard.Width = JoinCard.Width = Math.Min(720, width - 14);
        else HostCard.Width = JoinCard.Width = double.NaN;
    }

    private static void Place(Control c, int row, int column, int span)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
        Grid.SetColumnSpan(c, span);
    }

    private void ChatInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MultiplayerViewModel vm)
        {
            vm.SendChatCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void ImportComponent_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.F("multiplayer.component.import_picker", vm.ComponentFileName),
            FileTypeFilter = [new FilePickerFileType(L.T("multiplayer.component.import_filetype")) { Patterns = ["*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.ImportComponentAsync(path);
    }

    private async void ExportDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("multiplayer.diagnostics.export_title"),
            SuggestedFileName = L.F("multiplayer.diagnostics.file_name", DateTime.Now) + ".txt",
            DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportDiagnostics(path);
    }

    private async void ExportChat_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("multiplayer.chat.export_title"),
            SuggestedFileName = L.F("multiplayer.chat.file_name", DateTime.Now) + ".txt",
            DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportChat(path);
    }
}
