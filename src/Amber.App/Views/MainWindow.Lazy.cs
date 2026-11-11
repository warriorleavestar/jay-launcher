using Amber.App.Views.Controls;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Amber.App.Views;

/// <summary>
/// The overlays MainWindow builds on first open (<see cref="LazyHost"/>): help and activity drawers, the palette, the
/// drop layer, the breaker countdown and the first-run wizard. These properties keep the old names; reading one builds
/// its overlay, so startup code must not touch them (use <see cref="ApplyOverlayTier"/>). Inside a lazy host every
/// x:Name ends in "Part": the name generator still emits a field for it, which stays null, so use these properties.
/// </summary>
public partial class MainWindow
{
    internal Panel HelpLayer => HelpHost.Get<Panel>("HelpLayerPart");
    internal Border HelpDrawer => HelpHost.Get<Border>("HelpDrawerPart");
    internal TextBox HelpDrawerSearch => HelpHost.Get<TextBox>("HelpDrawerSearchPart");
    internal Panel ActivityLayer => ActivityHost.Get<Panel>("ActivityLayerPart");
    internal Border ActivityDrawer => ActivityHost.Get<Border>("ActivityDrawerPart");
    internal FirstRunWizard FirstRunLayer => FirstRunHost.Get<FirstRunWizard>("FirstRunLayerPart");
    internal Border DropLayer => DropHost.Get<Border>("DropLayerPart");
    internal ItemsControl DropTargets => DropHost.Get<ItemsControl>("DropTargetsPart");
    internal Panel PaletteLayer => PaletteHost.Get<Panel>("PaletteLayerPart");
    internal Border PalettePanel => PaletteHost.Get<Border>("PalettePanelPart");
    internal TextBox PaletteBox => PaletteHost.Get<TextBox>("PaletteBoxPart");
    internal ListBox PaletteList => PaletteHost.Get<ListBox>("PaletteListPart");
    internal ItemsControl ActivityTaskList => ActivityHost.Get<ItemsControl>("ActivityTaskListPart");
    internal TextBlock HelpNoResults => HelpHost.Get<TextBlock>("HelpNoResultsPart");
    internal TextBlock PaletteEmpty => PaletteHost.Get<TextBlock>("PaletteEmptyPart");
    internal Border BreakerOverlay => BreakerHost.Get<Border>("BreakerOverlayPart");

    private void InitLazyOverlays()
    {
        foreach (var host in new[] { HelpHost, ActivityHost, PaletteHost, DropHost })
            host.Built += _ => ApplyOverlayTier();
    }

    /// <summary>Drawer and palette widths for the current width tier; only overlays already built are touched.</summary>
    private void ApplyOverlayTier()
    {
        var compact = ContentTier == Amber.Core.Layout.WidthTier.Compact;
        if (ActivityHost.IsBuilt)
        {
            ActivityDrawer.Width = compact ? double.NaN : 400;
            ActivityDrawer.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        }
        if (HelpHost.IsBuilt)
        {
            HelpDrawer.Width = compact ? double.NaN : 400;
            HelpDrawer.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        }
        if (PaletteHost.IsBuilt)
        {
            PalettePanel.Width = compact ? double.NaN : 640;
            PalettePanel.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        }
        if (DropHost.IsBuilt) DropLayer.Classes.Set("compact", Bounds.Width < 640);
    }
}
