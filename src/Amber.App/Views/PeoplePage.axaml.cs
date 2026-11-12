using Avalonia.Controls;

namespace Amber.App.Views;

public partial class PeoplePage : UserControl
{
    /// <summary>Theme packs can lay this page's regions out differently (docs/themes.md, "page layouts").</summary>
    internal Amber.App.Services.ThemeLayoutHost ThemeLayout { get; }

    public PeoplePage()
    {
        InitializeComponent();
        ThemeLayout = Amber.App.Services.ThemeLayoutHost.Attach(this, "people");
    }
}
