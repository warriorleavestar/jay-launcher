using Avalonia;
using Avalonia.Controls;

namespace Amber.Mobile.Views;

public sealed partial class MainView : UserControl
{
    private const double TwoColumnWidth = 760;
    private bool _twoColumns;

    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => UpdateLayout(args.NewSize.Width);
    }

    private void UpdateLayout(double width)
    {
        PageContent.Margin = width < 360 ? new Thickness(10, 12, 10, 20) : new Thickness(16, 16, 16, 24);

        var twoColumns = width >= TwoColumnWidth;
        if (twoColumns == _twoColumns) return;

        _twoColumns = twoColumns;
        ContentColumns.ColumnDefinitions = new ColumnDefinitions(twoColumns ? "*,*" : "*");
        ContentColumns.RowDefinitions = new RowDefinitions(twoColumns ? "Auto" : "Auto,Auto");
        Grid.SetColumn(ServerColumn, twoColumns ? 1 : 0);
        Grid.SetRow(ServerColumn, twoColumns ? 0 : 1);
    }
}
