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
            if (_vm is not null) _vm.LineAdded -= ScrollToNewest;
            _vm = DataContext as PeopleViewModel;
            if (_vm is null) return;
            _vm.LineAdded += ScrollToNewest;
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
    }

    /// <summary>The least the add panel gets, however short the window: its header, my code and the box for theirs.</summary>
    internal const double MinAddPanelHeight = 150;

    /// <summary>
    /// The add panel sits in a row that is as tall as its content, and with the emoji check it is taller than a short
    /// window: it gets what is left of the page under its top edge, and scrolls inside.
    /// </summary>
    private void FitAddPanel()
    {
        if (!AddFriendCard.IsVisible || Bounds.Height <= 0) return;
        var top = AddFriendCard.TranslatePoint(default, this)?.Y ?? 0;
        AddFriendCard.MaxHeight = Math.Max(MinAddPanelHeight, Bounds.Height - top);
    }

    /// <summary>A new line in the open conversation: show it.</summary>
    private void ScrollToNewest() => Dispatcher.UIThread.Post(ConversationScroll.ScrollToEnd, DispatcherPriority.Background);
}
