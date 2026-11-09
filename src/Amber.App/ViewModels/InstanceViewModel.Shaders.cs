using Amber.Core.Content;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>The shader tab says when shader packs won't work and offers [Install Iris] (old-spec gap N3).</summary>
public sealed partial class InstanceViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShaderNote), nameof(HasShaderNote), nameof(CanInstallRenderer), nameof(InstallRendererLabel))]
    public partial ShaderReadiness? ShaderState { get; set; }

    public string? ShaderNote => ShaderState is { } s && Current is { } c ? ShaderRenderer.NoteFor(s, c.Id) : null;
    public bool HasShaderNote => ShaderNote is not null;
    public bool CanInstallRenderer => ShaderState is { } s && ShaderSupport.ProjectFor(s) is not null;
    public string InstallRendererLabel => L.F("content.shader.install", ShaderRenderer.NameFor(ShaderState ?? ShaderReadiness.NeedsIris));

    /// <summary>The generic "needs a shader loader" card, hidden while the more specific Iris / vanilla note above shows.</summary>
    public bool ShowShaderLoaderCard => !HasShaderLoader && !HasShaderNote;
    partial void OnShaderStateChanged(ShaderReadiness? value) => OnPropertyChanged(nameof(ShowShaderLoaderCard));
    partial void OnHasShaderLoaderChanged(bool value) => OnPropertyChanged(nameof(ShowShaderLoaderCard));

    internal void UpdateShaderState() => ShaderState = Current is { } c ? ShaderRenderer.ReadinessOf(c) : null;

    [RelayCommand]
    private Task InstallRendererAsync() => RunWorkAsync(async ct =>
    {
        if (Current is not { } instance || ShaderState is not { } state || ShaderSupport.ProjectFor(state) is null) return;
        var name = ShaderRenderer.NameFor(state);
        SetStatus(StatusMessage.Info("content.shader.installing", name));
        var ok = await ShaderRenderer.InstallAsync(_service, instance, state, ct);
        SetStatus(ok
            ? StatusMessage.Success("content.shader.renderer_installed", name, instance.Id)
            : StatusMessage.Warning("content.shader.renderer_unavailable", name, instance.McVersion, instance.Loader ?? ""));
        UpdateShaderState();
    });
}
