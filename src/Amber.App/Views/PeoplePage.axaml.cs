using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Contacts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

public partial class PeoplePage : UserControl
{
    /// <summary>Theme packs can lay this page's regions out differently (docs/themes.md, "page layouts").</summary>
    internal Amber.App.Services.ThemeLayoutHost ThemeLayout { get; }

    private PeopleViewModel? _vm;

    public PeoplePage()
    {
        InitializeComponent();
        ThemeLayout = Amber.App.Services.ThemeLayoutHost.Attach(this, "people");
        SizeChanged += (_, _) => FitAddPanel();
        AddFriendCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) Dispatcher.UIThread.Post(FitAddPanel, DispatcherPriority.Loaded);
        };
        DataContextChanged += (_, _) =>
        {
            Unhook();
            _vm = DataContext as PeopleViewModel;
            if (_vm is null) return;
            if (VisualRoot is not null) Hook();
            // The backup and restore of the friend list are in this page's [...]: it owns their file dialogs.
            _vm.Multiplayer.PickFriendBackupSave = async () =>
            {
                if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = L.T("multiplayer.friends.save_title"),
                    SuggestedFileName = "jay-friends" + FriendBackup.FileExtension,
                    DefaultExtension = FriendBackup.FileExtension,
                });
                return file?.TryGetLocalPath();
            };
            _vm.Multiplayer.PickFriendBackupOpen = async () =>
            {
                if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T("multiplayer.friends.open_title") });
                return files.Count > 0 ? files[0].TryGetLocalPath() : null;
            };
        };
        // The view model outlives this page (a new page is made on every visit): listen only while the page is shown.
        AttachedToVisualTree += (_, _) => Hook();
        DetachedFromVisualTree += (_, _) => Unhook();
    }

    private bool _hooked;

    private void Hook()
    {
        if (_vm is null || _hooked) return;
        _vm.LineAdded += ScrollToNewest;
        _vm.PropertyChanged += OnViewModelChanged;
        _hooked = true;
    }

    private void Unhook()
    {
        if (_vm is null || !_hooked) return;
        _vm.LineAdded -= ScrollToNewest;
        _vm.PropertyChanged -= OnViewModelChanged;
        _hooked = false;
    }

    /// <summary>
    /// The question about removing a friend takes the focus on [Keep], the safe answer; opening a friend's safety codes
    /// while a new device waits puts it on that device's [Not them? Remove].
    /// </summary>
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PeopleViewModel.IsConfirmingRemove) && _vm?.IsConfirmingRemove == true)
            Dispatcher.UIThread.Post(() => KeepFriendButton.Focus(), DispatcherPriority.Loaded);
        else if (e.PropertyName is nameof(PeopleViewModel.ShowSafety) or nameof(PeopleViewModel.Selected) && _vm?.ShowSafety == true)
            Dispatcher.UIThread.Post(FocusNewDevice, DispatcherPriority.Loaded);
    }

    private Contact? _focusedNewDevice;

    /// <summary>A new-device panel that just showed takes the focus once, on its safe answer.</summary>
    private void FocusNewDevice()
    {
        if (NewDeviceSafeButton() is not { DataContext: ContactDeviceItem item } safe || ReferenceEquals(item.Device, _focusedNewDevice)
            || _focusedNewDevice is { } was && was.PublicKey.AsSpan().SequenceEqual(item.Device.PublicKey)) return;
        _focusedNewDevice = item.Device;
        safe.Focus();
    }

    /// <summary>The safe answer of the first new-device panel, if one shows.</summary>
    internal Amber.App.Controls.Safety.SafetyButton? NewDeviceSafeButton() =>
        NewDeviceList.GetVisualDescendants().OfType<Amber.App.Controls.Safety.SafetyButton>().FirstOrDefault(b => b.Classes.Contains("device-new-remove") && b.IsEffectivelyVisible);

    /// <summary>The least the add panel gets, however short the window: its header, my code and the box for theirs.</summary>
    internal const double MinAddPanelHeight = 150;

    /// <summary>Under this much room for the add panel, the hint under my code is left out.</summary>
    internal const double RoomForMyCodeHint = 260;

    /// <summary>
    /// The add panel sits in a row that is as tall as its content, and with the emoji check it is taller than a short
    /// window: it gets what is left of the page under its top edge, and scrolls inside.
    /// </summary>
    private void FitAddPanel()
    {
        if (!AddFriendCard.IsVisible || Bounds.Height <= 0) return;
        var top = AddFriendCard.TranslatePoint(default, this)?.Y ?? 0;
        var room = Bounds.Height - top;
        AddFriendCard.MaxHeight = Math.Max(MinAddPanelHeight, room);
        // A short window: the line under "My friend code" goes (its tooltip keeps it), so the box for their code and
        // [Add] are in sight without scrolling, in every language.
        MyCodeHint.IsVisible = room >= RoomForMyCodeHint;
    }

    /// <summary>A new line in the open conversation: show it.</summary>
    private void ScrollToNewest() => Dispatcher.UIThread.Post(ConversationScroll.ScrollToEnd, DispatcherPriority.Background);
}
