using Amber.Core.Localization;
using Amber.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia;
using Avalonia.VisualTree;

namespace Amber.App.Views;

public partial class MultiplayerPage : UserControl
{
    /// <summary>主题包可以重新排这一页的区块（docs/themes.md 的“页面布局”）。</summary>
    internal Amber.App.Services.ThemeLayoutHost ThemeLayout { get; }

    public MultiplayerPage()
    {
        InitializeComponent();
        ThemeLayout = Amber.App.Services.ThemeLayoutHost.Attach(this, "multiplayer");
        SizeChanged += (_, e) =>
        {
            ApplyLobbyLayout(e.NewSize.Width);
            ApplyRoomLayout(e.NewSize.Width);
            // A knock never takes the whole page: past this it scrolls.
            KnockScroll.MaxHeight = Math.Max(180, e.NewSize.Height * 0.55);
        };
        AddHandler(KeyDownEvent, OnRoomKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
                vm.InviteFocusRequested = () => Dispatcher.UIThread.Post(() =>
                {
                    ApplyRoomLayout(Bounds.Width);
                    InviteCard.BringIntoView();
                    (CopyInviteButton.IsEffectivelyVisible ? CopyInviteButton : (Control)CreateFirstInviteButton).Focus();
                }, DispatcherPriority.Background);
                // A knock: "No" gets the focus (Enter or Space never lets anyone in by accident).
                vm.ApprovalCards.CollectionChanged += (_, e) =>
                {
                    // The next knock in line gets the same treatment once the first is answered.
                    if (vm.HasKnocks) Dispatcher.UIThread.Post(FocusKnockDeny, DispatcherPriority.Background);
                };
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(MultiplayerViewModel.CompactSection) or nameof(MultiplayerViewModel.Mode)) ApplyRoomLayout(Bounds.Width);
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

    /// <summary>Room console at the page's width: people | chat | details, or people | chat with details below, or one section at a time.</summary>
    internal RoomArrangement RoomLayout { get; private set; } = RoomArrangement.ThreeColumns;

    internal enum RoomArrangement { ThreeColumns, TwoColumns, Sections }

    private void ApplyRoomLayout(double width)
    {
        if (width <= 0) return;
        var layout = width >= 1000 ? RoomArrangement.ThreeColumns : width >= 620 ? RoomArrangement.TwoColumns : RoomArrangement.Sections;
        RoomLayout = layout;
        var section = (DataContext as MultiplayerViewModel)?.CompactSection ?? 0;
        RoomSections.IsVisible = layout == RoomArrangement.Sections;
        switch (layout)
        {
            case RoomArrangement.ThreeColumns:
                RoomGrid.ColumnDefinitions = new ColumnDefinitions("250,14,*,14,300");
                Place(PeopleColumn, 0, 0, 1);
                Place(ChatColumn, 0, 2, 1);
                Place(DetailsColumn, 0, 4, 1);
                DetailsColumn.Margin = new Avalonia.Thickness(0);
                break;
            case RoomArrangement.TwoColumns:
                RoomGrid.ColumnDefinitions = new ColumnDefinitions("230,14,*");
                Place(PeopleColumn, 0, 0, 1);
                Place(ChatColumn, 0, 2, 1);
                Place(DetailsColumn, 1, 0, 3);
                DetailsColumn.Margin = new Avalonia.Thickness(0, 14, 0, 0);
                break;
            default:
                RoomGrid.ColumnDefinitions = new ColumnDefinitions("*");
                Place(PeopleColumn, 0, 0, 1);
                Place(ChatColumn, 0, 0, 1);
                Place(DetailsColumn, 0, 0, 1);
                DetailsColumn.Margin = new Avalonia.Thickness(0);
                break;
        }
        var sections = layout == RoomArrangement.Sections;
        PeopleColumn.IsVisible = !sections || section == 0;
        ChatColumn.IsVisible = !sections || section == 1;
        DetailsColumn.IsVisible = !sections || section == 2;
        // Compact header: the buttons go under the room line.
        Grid.SetRow(RoomHeaderActions, sections ? 1 : 0);
        Grid.SetColumn(RoomHeaderActions, sections ? 0 : 1);
        RoomHeaderActions.HorizontalAlignment = sections ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        RoomHeaderActions.Margin = sections ? new Avalonia.Thickness(0, 6, 0, 0) : new Avalonia.Thickness(0);
    }

    /// <summary>The first knock's "No" button (default focus).</summary>
    internal Button? FirstKnockDeny => KnockList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("knock-deny"));

    private void FocusKnockDeny() => FirstKnockDeny?.Focus();

    /// <summary>Alt+Y lets the first knock in, Alt+N says no (Esc too, while the knock panel has the focus); Ctrl+Shift+I invites.</summary>
    private void OnRoomKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm) return;
        if (vm.HasKnocks)
        {
            if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Y) { vm.ApproveFirstKnockCommand.Execute(null); e.Handled = true; return; }
            if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.N) { vm.DenyFirstKnockCommand.Execute(null); e.Handled = true; return; }
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && e.Source is Visual source && KnockList.IsVisualAncestorOf(source))
            {
                vm.DenyFirstKnockCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.I && vm.IsHosting)
        {
            _ = vm.InviteFriendsCommand.ExecuteAsync(null);
            e.Handled = true;
        }
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
