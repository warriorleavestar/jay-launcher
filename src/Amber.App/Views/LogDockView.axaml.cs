using System.Collections.Specialized;
using System.ComponentModel;
using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>The live log, docked or popped out. Follows the newest line while "Follow" is on; F8 jumps to the next error.</summary>
public partial class LogDockView : UserControl
{
    private LogViewModel? _vm;
    private bool _scrollQueued;

    public LogDockView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    /// <summary>Buttons the host puts at the top right (pop out, dock, close).</summary>
    public Avalonia.Controls.Controls Buttons => DockButtons.Children;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Bind(DataContext as LogViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Bind(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null) Bind(DataContext as LogViewModel);
    }

    private void Bind(LogViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Lines.CollectionChanged -= OnLines;
            _vm.PropertyChanged -= OnVm;
            _vm.JumpRequested -= OnJump;
        }
        _vm = vm;
        if (vm is null) return;
        vm.Lines.CollectionChanged += OnLines;
        vm.PropertyChanged += OnVm;
        vm.JumpRequested += OnJump;
        QueueScroll();
    }

    private void OnLines(object? sender, NotifyCollectionChangedEventArgs e) => QueueScroll();

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LogViewModel.FollowLatest) or null) QueueScroll();
    }

    private void OnJump(LogEntry entry) => Dispatcher.UIThread.Post(() => DockList.ScrollIntoView(entry), DispatcherPriority.Background);

    private void QueueScroll()
    {
        if (_vm is not { FollowLatest: true } || _scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_vm is { FollowLatest: true, Lines.Count: > 0 } vm) DockList.ScrollIntoView(vm.Lines.Count - 1);
        }, DispatcherPriority.Background);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key != Key.F8 || e.KeyModifiers != KeyModifiers.None) return;
        _vm.NextErrorCommand.Execute(null);
        e.Handled = true;
    }
}
