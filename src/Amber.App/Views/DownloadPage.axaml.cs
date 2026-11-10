using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// The new-instance panel (official design §6.11). Wide: the sources are a rail on the left. Medium and compact: they
/// become a segment above the content, and the button bar stays at the bottom.
/// </summary>
public partial class DownloadPage : UserControl
{
    /// <summary>Below this page width the source rail turns into a segment on top.</summary>
    internal const double RailFrom = 820;

    /// <summary>主题包可以重新排这一页的区块（docs/themes.md 的“页面布局”）。</summary>
    internal Amber.App.Services.ThemeLayoutHost ThemeLayout { get; }

    public DownloadPage()
    {
        InitializeComponent();
        ThemeLayout = Amber.App.Services.ThemeLayoutHost.Attach(this, "downloads");
        // A click or arrow key on a row picks that version; a rebuilt list (search, filter) keeps the pick.
        VersionList.SelectionChanged += (_, e) =>
        {
            if (DataContext is DownloadViewModel vm && e.AddedItems is [RemoteVersionItem item, ..]) vm.ConfigureCommand.Execute(item);
        };
        // After a search or filter rebuilt the rows, highlight the chosen version again if it is still listed.
        // Subscribed only while the page is shown: the view model outlives the page.
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is not DownloadViewModel vm) return;
            (_items = vm.Items).CollectionChanged += OnItemsChanged;
            (_vm = vm).PropertyChanged += OnVmChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_items is not null) _items.CollectionChanged -= OnItemsChanged;
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _items = null;
            _vm = null;
        };
        VersionList.DoubleTapped += (_, e) =>
        {
            if (DataContext is DownloadViewModel vm && vm.InstallCommand.CanExecute(null)) vm.InstallCommand.Execute(null);
            e.Handled = true;
        };
        SizeChanged += (_, e) =>
        {
            var size = e.NewSize;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ApplyWidth(size.Width);
                ApplyHeight(size.Height);
            });
        };
        // The type chips, the search box and Refresh share one row until the chips leave the search box less than its
        // minimum width (long languages, narrow windows); then search and Refresh move to a second row instead of overlapping.
        SearchRow.SizeChanged += (_, _) => LayoutSearchRow();
        TypeChips.SizeChanged += (_, _) => LayoutSearchRow();
        RefreshButton.SizeChanged += (_, _) => LayoutSearchRow();
        // Loaders the chosen version can't have are greyed out, with the reason as a tooltip (§6.11).
        LoaderSegment.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ListBoxItem item) return;
            item.Bind(IsEnabledProperty, new Avalonia.Data.Binding(nameof(LoaderChoice.IsAvailable)));
            item.Bind(ToolTip.TipProperty, new Avalonia.Data.Binding(nameof(LoaderChoice.BlockReason)));
            ToolTip.SetShowOnDisabled(item, true);
        };
    }

    private System.Collections.ObjectModel.ObservableCollection<RemoteVersionItem>? _items;

    private DownloadViewModel? _vm;

    /// <summary>
    /// The panel was closed: drop the highlight too, or picking the same row again would not count as a change
    /// and the panel would not open.
    /// </summary>
    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadViewModel.Target) && _vm is { Target: null } && VersionList.SelectedItem is not null)
            VersionList.SelectedItem = null;
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is DownloadViewModel { Target: { } target } vm && VersionList.SelectedItem is null
                && vm.Items.FirstOrDefault(i => i.Id == target.Id) is { } row) VersionList.SelectedItem = row;
        });

    private bool? _rail;

    private void ApplyWidth(double width)
    {
        Classes.Set("compact", width < Amber.App.Services.ThemeLayoutHost.CompactBelow);
        var rail = width >= RailFrom;
        if (_rail == rail) return;
        _rail = rail;
        Grid.SetColumn(SourceList, rail ? 0 : 1);
        Grid.SetRowSpan(SourceList, rail ? 5 : 1);
        SourceList.Width = rail ? 190 : double.NaN;
        SourceList.Margin = rail ? new Thickness(0, 0, 14, 0) : new Thickness(0, 0, 0, 10);
        SourceList.ItemsPanel = rail
            ? new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 2 })
            : new FuncTemplate<Panel?>(() => new WrapPanel { ItemSpacing = 4, LineSpacing = 4 });
        SourceList.Classes.Set("subtabs", !rail);
    }

    private bool? _searchWrapped;

    internal bool SearchWrapped => _searchWrapped == true;

    private void LayoutSearchRow()
    {
        var available = SearchRow.Bounds.Width;
        if (available <= 0) return;
        var needed = TypeChips.DesiredSize.Width + SearchBox.MinWidth + SearchBox.Margin.Left + SearchBox.Margin.Right + RefreshButton.DesiredSize.Width;
        // A little slack so the row doesn't flip back and forth at the exact boundary.
        var wrap = _searchWrapped == true ? needed > available - 8 : needed > available;
        if (_searchWrapped == wrap) return;
        _searchWrapped = wrap;
        Grid.SetRow(SearchBox, wrap ? 1 : 0);
        Grid.SetColumn(SearchBox, wrap ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, wrap ? 2 : 1);
        SearchBox.Margin = wrap ? new Thickness(0, 8, 10, 0) : new Thickness(10, 0);
        Grid.SetRow(RefreshButton, wrap ? 1 : 0);
        RefreshButton.Margin = wrap ? new Thickness(0, 8, 0, 0) : default;
    }

    /// <summary>Below this page height the page scrolls and the version list gets a fixed height, so nothing is cut off.</summary>
    internal const double ScrollBelow = 620;

    private bool? _short;

    private void ApplyHeight(double height)
    {
        var isShort = height < ScrollBelow;
        if (_short == isShort) return;
        _short = isShort;
        PageScroll.VerticalScrollBarVisibility = isShort ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        VersionCard.Height = isShort ? 220 : double.NaN;
        Classes.Set("short", isShort);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not DownloadViewModel vm) return;
        if (e.Key == Key.Escape && !vm.IsBusy)
        {
            Close();
            e.Handled = true;
        }
        // Enter installs from the list or the name box (§6.11 keyboard); other controls keep their own Enter.
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && vm.IsVersionSource
                 && (e.Source is Control c && (c == NameBox || c.FindAncestorOfType<ListBox>() == VersionList || c == VersionList))
                 && vm.InstallCommand.CanExecute(null))
        {
            vm.InstallCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>✕ and Esc: back to where the panel was opened from (the library when it was opened directly).</summary>
    private void Close()
    {
        if (DataContext is DownloadViewModel vm) vm.CloseAfterStart?.Invoke();
    }

    private async void PickPack_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DownloadViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("newinstance.pack.pick"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.T("newinstance.pack.type")) { Patterns = ["*.mrpack", "*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.PreviewPackAsync(path);
    }

    private async void PickFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DownloadViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("newinstance.import.pick"), AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.UseGameDirectory(path);
    }
}
