using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace Amber.App.Views;

/// <summary>
/// 窗口外壳按主题包的 layout/shell.json 摆：导航放左、右、上、下，导航宽度，只显示图标，品牌区显不显示，
/// 内容最大宽度，导航项顺序和隐藏。没有主题包时是 Jay 自带的左侧栏。
/// </summary>
public partial class MainWindow
{
    public const double DefaultNavWidth = 180;

    private void AttachShell()
    {
        ThemeService.Changed += ApplyShell;
        Closed += (_, _) => ThemeService.Changed -= ApplyShell;
        DataContextChanged += (_, _) => ApplyShell();
        ApplyShell();
    }

    /// <summary>当前导航位置（自检用）。</summary>
    internal string ShellNav { get; private set; } = "left";

    private void ApplyShell()
    {
        var shell = ThemeService.Current?.Shell;
        (DataContext as MainViewModel)?.ApplyNavOrder(shell);
        var nav = shell?.Nav ?? "left";
        var icons = shell?.NavStyle == "icons";
        var width = shell?.NavWidth ?? (icons ? 64 : DefaultNavWidth);
        var horizontal = nav is "top" or "bottom";
        ShellNav = nav;

        ShellGrid.ColumnDefinitions.Clear();
        ShellGrid.RowDefinitions.Clear();
        if (horizontal)
        {
            ShellGrid.RowDefinitions = nav == "top" ? new RowDefinitions("Auto,*") : new RowDefinitions("*,Auto");
            Place(SidebarHost, nav == "top" ? 0 : 1, 0);
            Place(PageHost, nav == "top" ? 1 : 0, 0);
        }
        else
        {
            ShellGrid.ColumnDefinitions = nav == "right" ? new ColumnDefinitions($"*,{width}") : new ColumnDefinitions($"{width},*");
            Place(SidebarHost, 0, nav == "right" ? 1 : 0);
            Place(PageHost, 0, nav == "right" ? 0 : 1);
        }
        Grid.SetRowSpan(ThemeBackground, 2);
        Grid.SetColumnSpan(ThemeBackground, 2);

        // 侧栏里面：竖排是 标题 / 导航 / 版本 三行；横排是 标题 | 导航 | 版本 三列。
        SidebarGrid.RowDefinitions.Clear();
        SidebarGrid.ColumnDefinitions.Clear();
        if (horizontal)
        {
            SidebarGrid.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
            Place(SidebarHeader, 0, 0);
            Place(NavList, 0, 1);
            Place(SidebarFooter, 0, 2);
            NavList.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 });
            ScrollViewer.SetHorizontalScrollBarVisibility(NavList, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(NavList, ScrollBarVisibility.Disabled);
            NavList.Margin = new Thickness(4, 6);
            SidebarFooter.IsVisible = false;
        }
        else
        {
            SidebarGrid.RowDefinitions = new RowDefinitions("Auto,*,Auto");
            Place(SidebarHeader, 0, 0);
            Place(NavList, 1, 0);
            Place(SidebarFooter, 2, 0);
            NavList.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());
            ScrollViewer.SetHorizontalScrollBarVisibility(NavList, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(NavList, ScrollBarVisibility.Auto);
            NavList.Margin = new Thickness(8, 0);
            SidebarFooter.IsVisible = !icons;
        }
        NavList.Classes.Set("icons", icons);
        SidebarHost.Classes.Set("horizontal", horizontal);
        // 品牌区：图标模式只留 Jay 的图标，文字收起来。
        SidebarHeader.IsVisible = shell?.ShowBrand ?? true;
        BrandName.IsVisible = !icons;
        BrandSubtitle.IsVisible = !icons && !horizontal;
        if (horizontal) SidebarHeader.Margin = new Thickness(16, 6, 8, 6);
        else if (icons) SidebarHeader.Margin = new Thickness(14, 18, 8, 12);
        else SidebarHeader.ClearValue(MarginProperty); // 交给侧栏的样式（窗口矮时收紧）

        PageHost.MaxWidth = shell?.ContentMaxWidth ?? double.PositiveInfinity;
    }

    private static void Place(Control c, int row, int column)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
    }
}
