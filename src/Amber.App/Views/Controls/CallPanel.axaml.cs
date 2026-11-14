using Avalonia.Controls;
using Avalonia.Threading;

namespace Amber.App.Views.Controls;

public partial class CallPanel : UserControl
{
    public CallPanel()
    {
        InitializeComponent();
        // The locked confirmation opens with the focus on [Don't allow], like every other safety question.
        ControlConfirm.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && ControlConfirm.IsVisible)
                Dispatcher.UIThread.Post(() => ControlRefuse.Focus(), DispatcherPriority.Loaded);
        };
    }
}
