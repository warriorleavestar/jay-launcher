using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Amber.Mobile.Views;

public sealed partial class MainView : UserControl
{
    private const double PhoneWidth = 600;
    private const double ExpandedSidebarWidth = 900;
    private const double TwoColumnWidth = 680;
    private bool _twoColumns = true;
    private string _page = "home";

    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => UpdateLayout(args.NewSize.Width);
        DataContextChanged += (_, _) => UpdateNavigation();
        UpdateNavigation();
    }

    private void UpdateLayout(double width)
    {
        var phone = width < PhoneWidth;
        var rail = !phone && width < ExpandedSidebarWidth;
        SidebarHost.IsVisible = !phone;
        BottomNav.IsVisible = phone;
        ShellGrid.ColumnDefinitions = new ColumnDefinitions(phone ? "*" : rail ? "84,*" : "232,*");
        Grid.SetColumn(ContentHost, phone ? 0 : 1);
        BrandText.IsVisible = !rail;
        foreach (var button in new[] { SideHome, SideLibrary, SideMultiplayer, SideSettings, SideMe })
        {
            button.HorizontalContentAlignment = rail
                ? Avalonia.Layout.HorizontalAlignment.Center
                : Avalonia.Layout.HorizontalAlignment.Stretch;
            if (button.Content is Grid row && row.Children.Count > 1)
                row.Children[1].IsVisible = !rail;
        }

        PageContent.Margin = width < 360 ? new Thickness(10, 12, 10, 20) : phone
            ? new Thickness(14, 16, 14, 24)
            : new Thickness(24, 22, 24, 32);
        HomeHeroGrid.ColumnDefinitions = new ColumnDefinitions(phone ? "*" : "*,Auto");
        HomeHeroGrid.RowDefinitions = new RowDefinitions(phone ? "Auto,Auto" : "Auto");
        Grid.SetColumn(HomePlayButton, phone ? 0 : 1);
        Grid.SetRow(HomePlayButton, phone ? 1 : 0);
        HomePlayButton.HorizontalAlignment = phone
            ? Avalonia.Layout.HorizontalAlignment.Stretch
            : Avalonia.Layout.HorizontalAlignment.Right;

        var contentWidth = phone ? width : width - (rail ? 84 : 232);
        var twoColumns = contentWidth >= TwoColumnWidth;
        if (twoColumns == _twoColumns) return;

        _twoColumns = twoColumns;
        ContentColumns.ColumnDefinitions = new ColumnDefinitions(twoColumns ? "*,*" : "*");
        ContentColumns.RowDefinitions = new RowDefinitions(twoColumns ? "Auto" : "Auto,Auto");
        Grid.SetColumn(ServerColumn, twoColumns ? 1 : 0);
        Grid.SetRow(ServerColumn, twoColumns ? 0 : 1);

        HomeColumns.ColumnDefinitions = new ColumnDefinitions(twoColumns ? "*,*" : "*");
        HomeColumns.RowDefinitions = new RowDefinitions(twoColumns ? "Auto" : "Auto,Auto");
        Grid.SetColumn(HomeColumns.Children[1], twoColumns ? 1 : 0);
        Grid.SetRow(HomeColumns.Children[1], twoColumns ? 0 : 1);
        LibraryColumns.ColumnDefinitions = new ColumnDefinitions(twoColumns ? "1.35*,*" : "*");
        LibraryColumns.RowDefinitions = new RowDefinitions(twoColumns ? "Auto" : "Auto,Auto");
        Grid.SetColumn(LibraryColumns.Children[1], twoColumns ? 1 : 0);
        Grid.SetRow(LibraryColumns.Children[1], twoColumns ? 0 : 1);
    }

    private void OnNavigate(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string page }) return;
        _page = page;
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        HomePage.IsVisible = _page == "home";
        LibraryPage.IsVisible = _page == "library";
        MultiplayerPage.IsVisible = _page == "multiplayer";
        SettingsPage.IsVisible = _page == "settings";
        MePage.IsVisible = _page == "me";

        var viewModel = DataContext as ViewModels.MobileMainViewModel;
        PageTitle.Text = _page switch
        {
            "library" => viewModel?.LibraryText ?? "游戏库",
            "multiplayer" => viewModel?.MultiplayerText ?? "联机",
            "settings" => viewModel?.SettingsText ?? "设置",
            "me" => viewModel?.MeText ?? "我",
            _ => viewModel?.HomeText ?? "启动"
        };

        foreach (var (button, page) in new (Button Button, string Page)[]
        {
            (SideHome, "home"), (SideLibrary, "library"), (SideMultiplayer, "multiplayer"),
            (SideSettings, "settings"), (SideMe, "me"), (BottomHome, "home"),
            (BottomLibrary, "library"), (BottomMultiplayer, "multiplayer"),
            (BottomSettings, "settings"), (BottomMe, "me")
        })
            button.Classes.Set("active", page == _page);
    }
}
