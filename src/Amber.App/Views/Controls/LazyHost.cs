using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Metadata;

namespace Amber.App.Views.Controls;

/// <summary>
/// Builds its content the first time <see cref="IsOpen"/> turns true, then keeps it. Hidden overlays (drawers, the
/// palette, the drop layer, the first-run wizard) cost window build time on every start even though most sessions
/// never open them. Names inside the template live in their own scope: look them up with <see cref="Get{T}"/>.
/// </summary>
public sealed class LazyHost : Decorator
{
    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<LazyHost, bool>(nameof(IsOpen));

    private INameScope? _names;

    /// <summary>The deferred content: a <c>&lt;Template&gt;</c> in XAML.</summary>
    [Content]
    public Template? Content { get; set; }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public bool IsBuilt => _names is not null;

    /// <summary>Raised once, right after the content was built and attached.</summary>
    public event Action<LazyHost>? Built;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty && IsOpen) Ensure();
    }

    /// <summary>Builds the content now if it isn't yet.</summary>
    public void Ensure()
    {
        if (_names is not null || Content?.Content is not { } content) return;
        var result = TemplateContent.Load(content) ?? throw new InvalidOperationException("LazyHost template produced nothing.");
        _names = result.NameScope;
        Child = result.Result;
        Built?.Invoke(this);
    }

    /// <summary>A named control from the content, building the content first if needed.</summary>
    public T Get<T>(string name) where T : class
    {
        Ensure();
        return _names?.Find<T>(name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the lazy content.");
    }
}
