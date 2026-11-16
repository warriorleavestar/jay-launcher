using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Amber.App.Views;

public sealed partial class MobileLauncherView : UserControl
{
    public MobileLauncherView() => AvaloniaXamlLoader.Load(this);
}
