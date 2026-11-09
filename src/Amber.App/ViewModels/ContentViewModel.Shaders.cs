using Amber.App.Localization;
using Amber.Core.Content;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amber.App.ViewModels;

/// <summary>Installing a shader pack checks for Iris / Oculus / OptiFine first (old-spec gap N3).</summary>
public sealed partial class ContentViewModel
{
    /// <summary>Shader readiness of the "Install to" instance while browsing shaders; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShaderNote), nameof(HasShaderNote), nameof(CanInstallRenderer), nameof(AlsoInstallRendererLabel))]
    public partial ShaderReadiness? ShaderState { get; set; }

    public string? ShaderNote => ShaderState is { } s && TargetInstance is { } id ? ShaderRenderer.NoteFor(s, id) : null;
    public bool HasShaderNote => ShaderNote is not null;
    public bool CanInstallRenderer => ShaderState is { } s && ShaderSupport.ProjectFor(s) is not null;
    public string AlsoInstallRendererLabel => L.F("content.shader.also_install", ShaderRenderer.NameFor(ShaderState ?? ShaderReadiness.NeedsIris));

    /// <summary>[Also install Iris] next to the install button; on by default.</summary>
    [ObservableProperty] public partial bool AlsoInstallRenderer { get; set; } = true;

    internal void UpdateShaderState() =>
        ShaderState = CurrentType == ContentType.Shader && CurrentInstance() is { } instance ? ShaderRenderer.ReadinessOf(instance) : null;

    /// <summary>After a shader pack is in: install Iris / Oculus when asked, otherwise say shaders won't show yet.</summary>
    private async Task AfterShaderInstallAsync(GameInstance instance, ShaderReadiness state, string shader, CancellationToken ct)
    {
        if (ShaderSupport.ProjectFor(state) is null)
        {
            if (state == ShaderReadiness.Unsupported) SetStatus(StatusMessage.Warning("content.shader.installed_unsupported", shader, instance.Id));
            UpdateShaderState();
            return;
        }
        var name = ShaderRenderer.NameFor(state);
        if (!AlsoInstallRenderer)
        {
            SetStatus(StatusMessage.Warning("content.shader.installed_without", shader, instance.Id, name));
            UpdateShaderState();
            return;
        }
        SetStatus(StatusMessage.Info("content.shader.installing", name));
        var ok = await ShaderRenderer.InstallAsync(_service, instance, state, ct);
        SetStatus(ok
            ? StatusMessage.Success("content.shader.installed_with", shader, name, instance.Id)
            : StatusMessage.Warning("content.shader.renderer_unavailable", name, instance.McVersion, instance.Loader ?? ""));
        UpdateShaderState();
    }
}
