using System.ComponentModel;
using Amber.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Amber.App.Views.Controls;

/// <summary>The server's advanced settings (privacy level, relays, entry port rotation, address record).</summary>
public partial class HostServerAdvancedPanel : UserControl
{
    private HostServerViewModel? _vm;
    private bool _syncing;

    public HostServerAdvancedPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
            _vm = DataContext as HostServerViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
            SyncPrivacy();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostServerViewModel.PrivacyChoice)) SyncPrivacy();
    }

    /// <summary>
    /// The privacy radio buttons are set from code, not bound: a refused level has to put the checked button back, and a
    /// two-way binding keeps showing the refused one (the source value it reads back is the one it last saw).
    /// </summary>
    private void SyncPrivacy()
    {
        if (_vm is null) return;
        _syncing = true;
        try
        {
            HostPrivacyBasic.IsChecked = _vm.PrivacyChoice == 0;
            HostPrivacyPlus.IsChecked = _vm.PrivacyChoice == 1;
            HostPrivacyPlusPlus.IsChecked = _vm.PrivacyChoice == 2;
        }
        finally { _syncing = false; }
    }

    private void OnPrivacyChecked(object? sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null || sender is not RadioButton { IsChecked: true, Tag: string tag } || !int.TryParse(tag, out var choice)) return;
        _vm.ChoosePrivacy(choice);
        Dispatcher.UIThread.Post(SyncPrivacy);
    }
}
