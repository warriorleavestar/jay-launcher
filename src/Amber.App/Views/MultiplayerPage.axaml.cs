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
            // The diagnostics drawer covers the page in compact windows.
            DiagnosticsDrawer.Width = e.NewSize.Width < 640 ? double.NaN : 560;
            DiagnosticsDrawer.HorizontalAlignment = e.NewSize.Width < 640 ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Right;
            DrillDrawer.Width = DiagnosticsDrawer.Width;
            DrillDrawer.HorizontalAlignment = DiagnosticsDrawer.HorizontalAlignment;
            LessonDrawer.Width = DiagnosticsDrawer.Width;
            LessonDrawer.HorizontalAlignment = DiagnosticsDrawer.HorizontalAlignment;
            // Pictures hide in short windows (official §4.9).
            LessonArt.IsVisible = e.NewSize.Height >= 560;
            // Short windows: the join explanation folds away so "open a room" and its button come up higher.
            JoinHintText.IsVisible = e.NewSize.Height >= 620;
        };
        foreach (var box in (ComboBox[])[OpenWhoBox, OpenRouteBox, OpenProtectionBox, OpenScopeBox]) box.SelectionChanged += OnOpenChoiceChanged;
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
                vm.WhoSeesFocusRequested = () => Dispatcher.UIThread.Post(() =>
                {
                    ApplyRoomLayout(Bounds.Width);
                    WhoSeesCard.BringIntoView();
                    (RelayOnlyBox.IsEffectivelyEnabled ? RelayOnlyBox : (Control)WhoSeesCloseButton).Focus();
                }, DispatcherPriority.Background);
                SyncRoomSwitches(vm);
                // A knock: "No" gets the focus (Enter or Space never lets anyone in by accident).
                vm.ApprovalCards.CollectionChanged += (_, e) =>
                {
                    // The next knock in line gets the same treatment once the first is answered.
                    if (vm.HasKnocks) Dispatcher.UIThread.Post(FocusKnockDeny, DispatcherPriority.Background);
                };
                // Lessons: the focus follows the main button, so Enter pages through.
                vm.LessonFocusRequested = () => Dispatcher.UIThread.Post(() =>
                {
                    if (vm.LessonOpen) (vm.LessonIsLast ? LessonDoneButton : LessonNextButton).Focus();
                }, DispatcherPriority.Background);
                // Drill: each new question starts on "No" (the safe default, like a real knock); the lesson starts on Next.
                vm.DrillFocusRequested = () => Dispatcher.UIThread.Post(() =>
                {
                    if (!vm.DrillOpen) return;
                    if (vm.DrillAsking) DrillNoButton.Focus();
                    else if (vm.DrillExplaining) DrillNextButton.Focus();
                    else DrillRestartButton.Focus();
                }, DispatcherPriority.Background);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MultiplayerViewModel.DiagnosticsOpen) && vm.DiagnosticsOpen)
                        Dispatcher.UIThread.Post(() => DiagnosticsCheckButton.Focus(), DispatcherPriority.Background);
                    if (e.PropertyName is nameof(MultiplayerViewModel.CompactSection) or nameof(MultiplayerViewModel.Mode)) ApplyRoomLayout(Bounds.Width);
                    if (e.PropertyName is nameof(MultiplayerViewModel.MyRelayOnly) or nameof(MultiplayerViewModel.SharingGameProxy)) SyncRoomSwitches(vm);
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

    /// <summary>
    /// Lobby cards at the page's width. Join is always on top across the whole width; under it "open a room" and
    /// "where to play" sit side by side when there is room, else one under the other.
    /// </summary>
    internal LobbyArrangement LobbyLayout { get; private set; } = LobbyArrangement.SideBySide;

    internal enum LobbyArrangement { SideBySide, Stacked, Compact }

    private void ApplyLobbyLayout(double width)
    {
        var layout = width >= 820 ? LobbyArrangement.SideBySide : width >= 560 ? LobbyArrangement.Stacked : LobbyArrangement.Compact;
        LobbyLayout = layout;
        var wide = layout == LobbyArrangement.SideBySide;
        LobbyGrid.ColumnDefinitions = new ColumnDefinitions(wide ? "*,14,*" : "*");
        var span = wide ? 3 : 1;
        Place(JoinCard, 0, 0, span);
        Place(HostCard, 1, 0, 1);
        Place(WhereCard, wide ? 1 : 2, wide ? 2 : 0, 1);
        Place(LobbyFooter, 3, 0, span);
        // Wide: a join button as wide as the page would shout; it sits at the end of the card instead.
        LobbyJoinButton.HorizontalAlignment = wide ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
        LobbyJoinButton.MinWidth = wide ? 240 : 0;
        // Stacked: the cards keep a readable width in the middle of the page.
        var centred = layout == LobbyArrangement.Stacked;
        foreach (var card in (Border[])[JoinCard, HostCard, WhereCard])
        {
            card.MaxWidth = centred ? 720 : double.PositiveInfinity;
            card.HorizontalAlignment = centred ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Stretch;
            card.Width = centred ? Math.Min(720, width - 14) : double.NaN;
        }
    }

    /// <summary>
    /// A greyed-out option of the four open-a-room choices cannot be picked by any route (keyboard, type-ahead): the
    /// drop-down goes back to what it showed. The view model ignores such a value too.
    /// </summary>
    private static void OnOpenChoiceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: OpenChoice { IsEnabled: false } } box)
            box.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
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
        RoomHeaderActions.Margin = sections ? new Avalonia.Thickness(0, 8, 0, 0) : new Avalonia.Thickness(0);
        // Compact: the faces are in the people section already, and the header must leave room for the page.
        RoomFacesRow.IsVisible = !sections;
    }

    /// <summary>
    /// The two switches that restart the multiplayer component ("relays only", "forward their game") show what is true
    /// now, not what was clicked: a click asks the view model, and the box follows it when it is done (or refused).
    /// </summary>
    private void SyncRoomSwitches(MultiplayerViewModel vm)
    {
        RelayOnlyBox.IsChecked = vm.MyRelayOnly;
        ShareProxyBox.IsChecked = vm.SharingGameProxy;
    }

    private void RelayOnly_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MultiplayerViewModel vm) vm.MyRelayOnly = RelayOnlyBox.IsChecked == true;
    }

    private void ShareProxy_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MultiplayerViewModel vm) vm.SharingGameProxy = ShareProxyBox.IsChecked == true;
    }

    /// <summary>The first knock's "No" button (default focus).</summary>
    internal Button? FirstKnockDeny => KnockList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("knock-deny"));

    private void FocusKnockDeny() => FirstKnockDeny?.Focus();

    /// <summary>Alt+Y lets the first knock in, Alt+N says no (Esc too, while the knock panel has the focus); Ctrl+Shift+I invites.</summary>
    private void OnRoomKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm) return;
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.D)
        {
            vm.DiagnosticsOpen = !vm.DiagnosticsOpen;
            e.Handled = true;
            return;
        }
        // Public rooms in "where to play": F5 refreshes, "/" jumps to the search box (not while typing).
        if (vm.IsPlayTab && vm.IsIdle && vm.ShowPublicRoomList)
        {
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None) { vm.RefreshRoomsCommand.Execute(null); e.Handled = true; return; }
            if (e.Key is Key.Oem2 or Key.Divide && e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox)
            {
                RoomSearchBox.BringIntoView();
                RoomSearchBox.Focus();
                e.Handled = true;
                return;
            }
        }
        if (vm.LessonOpen && e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox)
        {
            if (e.Key == Key.Escape) vm.LessonOpen = false;
            else if (e.Key == Key.Right) { if (vm.LessonHasNext) vm.LessonNextCommand.Execute(null); }
            else if (e.Key == Key.Left) vm.LessonPreviousCommand.Execute(null);
            else return;
            e.Handled = true;
            return;
        }
        if (vm.DrillOpen && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            vm.DrillOpen = false;
            e.Handled = true;
            return;
        }
        if (vm.DiagnosticsOpen && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            vm.DiagnosticsOpen = false;
            e.Handled = true;
            return;
        }
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

    /// <summary>Server console: Enter sends, ↑↓ walk through earlier commands.</summary>
    private void ServerCommand_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel { HostServer: { } server } || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                server.SendCommandCommand.Execute(null);
                break;
            case Key.Up:
                server.HistoryUp();
                break;
            case Key.Down:
                server.HistoryDown();
                break;
            default:
                return;
        }
        if (sender is TextBox box) box.CaretIndex = box.Text?.Length ?? 0;
        e.Handled = true;
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
        if (DataContext is MultiplayerViewModel vm) await MultiplayerSettingsView.ImportComponentAsync(this, vm);
    }

    private async void BrowseDownloadFolder_Click(object? sender, RoutedEventArgs e)
    {
        if ((DataContext as MultiplayerViewModel)?.Tools is not { } tools || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("tools.download.folder_picker") });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) tools.DownloadFolder = path;
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
