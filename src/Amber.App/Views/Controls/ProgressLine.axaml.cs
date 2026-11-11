using System.Windows.Input;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace Amber.App.Views.Controls;

/// <summary>
/// The one progress line: name, current step, percent, speed, time left and [Cancel]. Bind what is known; a part
/// without a value is left out. <see cref="Percent"/> below zero (or <see cref="IsIndeterminate"/>) means the length
/// is not known.
/// </summary>
public partial class ProgressLine : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<ProgressLine, string?>(nameof(Title));
    public static readonly StyledProperty<string?> StepProperty = AvaloniaProperty.Register<ProgressLine, string?>(nameof(Step));
    public static readonly StyledProperty<double> PercentProperty = AvaloniaProperty.Register<ProgressLine, double>(nameof(Percent));
    public static readonly StyledProperty<bool> IsIndeterminateProperty = AvaloniaProperty.Register<ProgressLine, bool>(nameof(IsIndeterminate));
    public static readonly StyledProperty<double?> BytesPerSecondProperty = AvaloniaProperty.Register<ProgressLine, double?>(nameof(BytesPerSecond));
    public static readonly StyledProperty<TimeSpan?> RemainingProperty = AvaloniaProperty.Register<ProgressLine, TimeSpan?>(nameof(Remaining));
    public static readonly StyledProperty<ICommand?> CancelCommandProperty = AvaloniaProperty.Register<ProgressLine, ICommand?>(nameof(CancelCommand));
    public static readonly StyledProperty<bool> CanCancelProperty = AvaloniaProperty.Register<ProgressLine, bool>(nameof(CanCancel), true);

    /// <summary>What is being worked on ("Fabric 1.21.1", "Sodium").</summary>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>The current step ("Downloading game files 312 / 480").</summary>
    public string? Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    /// <summary>0–100.</summary>
    public double Percent { get => GetValue(PercentProperty); set => SetValue(PercentProperty, value); }

    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }

    public double? BytesPerSecond { get => GetValue(BytesPerSecondProperty); set => SetValue(BytesPerSecondProperty, value); }

    public TimeSpan? Remaining { get => GetValue(RemainingProperty); set => SetValue(RemainingProperty, value); }

    /// <summary>[Cancel] shows when a command is bound and <see cref="CanCancel"/> is true.</summary>
    public ICommand? CancelCommand { get => GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }

    public bool CanCancel { get => GetValue(CanCancelProperty); set => SetValue(CanCancelProperty, value); }

    /// <summary>The right-hand facts as shown ("37% · 4.2 MB/s · about 40 s"); self-checks read it.</summary>
    public string Facts => FactsText.Text ?? "";

    public ProgressLine()
    {
        InitializeComponent();
        // Subscribed only while on screen: L.Changed is static and would keep the control alive.
        AttachedToVisualTree += (_, _) => { L.Changed += OnLanguageChanged; Update(); };
        DetachedFromVisualTree += (_, _) => L.Changed -= OnLanguageChanged;
        Update();
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Update);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == StepProperty || change.Property == PercentProperty
            || change.Property == IsIndeterminateProperty || change.Property == BytesPerSecondProperty
            || change.Property == RemainingProperty || change.Property == CancelCommandProperty || change.Property == CanCancelProperty)
            Update();
    }

    private void Update()
    {
        if (TitleText is null) return;
        TitleText.Text = Title;
        TitleText.IsVisible = !string.IsNullOrEmpty(Title);
        TitleText.SetValue(ToolTip.TipProperty, Title);
        StepText.Text = Step;
        StepText.IsVisible = !string.IsNullOrEmpty(Step);
        StepText.SetValue(ToolTip.TipProperty, Step);
        CancelButton.Command = CancelCommand;
        var unknown = IsIndeterminate || Percent < 0;
        Bar.IsIndeterminate = unknown;
        Bar.Value = unknown ? 0 : Math.Clamp(Percent, 0, 100);
        FactsText.Text = FactsOf(unknown ? null : Percent, BytesPerSecond, Remaining);
        FactsText.IsVisible = FactsText.Text.Length > 0;
        CancelButton.IsVisible = CancelCommand is not null && CanCancel;
        AutomationProperties.SetName(Bar, string.Join(" ", new[] { Title, Step }.Where(t => !string.IsNullOrEmpty(t))));
    }

    /// <summary>"37% · 4.2 MB/s · about 40 s": each part only when it is known.</summary>
    public static string FactsOf(double? percent, double? bytesPerSecond, TimeSpan? remaining)
    {
        var parts = new List<string>(3);
        if (percent is { } p) parts.Add(string.Format(L.Culture, "{0:0}%", Math.Clamp(p, 0, 100)));
        if (bytesPerSecond is { } speed && speed > 0) parts.Add(Fmt.Speed(speed));
        if (remaining is { } left && left > TimeSpan.Zero) parts.Add(Fmt.Remaining(left));
        return string.Join(" · ", parts);
    }
}
