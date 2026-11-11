using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>
/// The first-run wizard's layer (official design §6.1). Width: the card is min(640, area − 48), compact windows get a
/// "Step 1 of 2" line instead of the dots and a full-width button. Short windows hide the tips. Enter continues;
/// Esc only closes an open drop-down, never the wizard.
/// </summary>
public partial class FirstRunWizard : UserControl
{
    internal const double CompactBelow = 640, ShortBelow = 560;

    private FirstRunViewModel? _vm;

    public FirstRunWizard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.FocusName -= FocusName;
            _vm = DataContext as FirstRunViewModel;
            if (_vm is null) return;
            _vm.FocusName += FocusName;
            _vm.PickPack = PickPackAsync;
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FirstRunViewModel.IsOpen) && _vm.IsOpen) FocusFirst();
                if (e.PropertyName == nameof(FirstRunViewModel.Step)) FocusFirst();
            };
            // MainWindow builds the wizard on first open (LazyHost), so it may arrive already open.
            if (_vm.IsOpen) FocusFirst();
        };
        SizeChanged += (_, e) => ApplySize(e.NewSize);
    }

    private void ApplySize(Size size)
    {
        var compact = size.Width < CompactBelow;
        StepDots.IsVisible = !compact;
        StepLine.IsVisible = compact;
        WizardCard.Margin = compact ? new Thickness(12, 0, 12, 12) : new Thickness(24, 0, 24, 16);
        ButtonBar.Padding = compact ? new Thickness(12, 8) : new Thickness(24, 10);
        InstallPlayButton.HorizontalAlignment = compact ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Right;
        ContinueButton.HorizontalAlignment = InstallPlayButton.HorizontalAlignment;
        Grid.SetColumn(ContinueButton, compact ? 1 : 2);
        Grid.SetColumnSpan(ContinueButton, compact ? 2 : 1);
        Grid.SetColumn(InstallPlayButton, compact ? 1 : 2);
        Grid.SetColumnSpan(InstallPlayButton, compact ? 2 : 1);
        InstallPlayButton.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        TipCard.IsVisible = size.Height >= ShortBelow;
        // The buttons stay in the card on roomy windows and stick to the bottom on compact or short ones.
        var sticky = compact || size.Height < ShortBelow;
        if (sticky && ButtonBar.Child != ButtonGrid)
        {
            CardButtonHost.Child = null;
            ButtonBar.Child = ButtonGrid;
        }
        else if (!sticky && CardButtonHost.Child != ButtonGrid)
        {
            ButtonBar.Child = null;
            CardButtonHost.Child = ButtonGrid;
        }
        ButtonBar.IsVisible = sticky;
    }

    private void FocusFirst() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm is not { IsOpen: true }) return;
        if (_vm.IsAccountStep) NameBox.Focus();
        else ChoiceList.Focus();
    }, DispatcherPriority.Background);

    private void FocusName() => Dispatcher.UIThread.Post(() => NameBox.Focus());

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is not { IsOpen: true }) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && e.Source is not Button)
        {
            if (_vm.ContinueCommand.CanExecute(null)) _vm.ContinueCommand.Execute(null);
            e.Handled = true;
        }
        // Esc never leaves the wizard by accident (an open drop-down closes itself first).
        else if (e.Key == Key.Escape) e.Handled = true;
    }

    private async Task<string?> PickPackAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("newinstance.pack.pick"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.T("newinstance.pack.type")) { Patterns = ["*.mrpack", "*.zip"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
