using Amber.App.Localization;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Files;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// One file sent or received: a card in the conversation and in the room, and a task in Activity (design: "What a file
/// looks like in a conversation"). The line under the name says where it is; the buttons are what that state allows.
/// </summary>
public sealed partial class TransferItem : ObservableObject
{
    private readonly MultiplayerViewModel? _owner;
    private DateTimeOffset _sampleTime;
    private long _sampleDone;

    public TransferItem(string id, bool outgoing, string name, long size, MultiplayerViewModel? owner = null, FileKind? kind = null, string peerName = "")
    {
        Id = id;
        Outgoing = outgoing;
        Name = name;
        Size = size;
        Kind = kind ?? FileKinds.Of(name);
        PeerName = peerName;
        _owner = owner;
        State = outgoing ? FileCardState.Waiting : FileCardState.Sending;
        LanguageListeners.Add(this, static o => ((TransferItem)o).Changed());
    }

    public string Id { get; }
    public bool Outgoing { get; }
    public string Name { get; }
    public long Size { get; internal set; }
    public FileKind Kind { get; internal set; }
    public string PeerName { get; internal set; }
    public FileCardState State { get; internal set; }
    public long Done { get; internal set; }
    public string? SavedPath { get; internal set; }
    public string? Error { get; internal set; }
    public bool Finished { get; internal set; }
    /// <summary>"Direct" or "Relayed", while it moves.</summary>
    public string Route { get; internal set; } = "";
    /// <summary>Bytes per second, smoothed; null before there are two samples.</summary>
    public double? Speed { get; private set; }
    /// <summary>What [Compare mods] found, on a received mod list.</summary>
    public string CompareText { get; internal set; } = "";

    // ---- what the sending side needs ----
    /// <summary>The friend (their person key) or room member ("fp:" + fingerprint) this goes to or came from.</summary>
    internal string? Person { get; init; }
    internal string? Fingerprint { get; set; }
    /// <summary>The file that actually goes out (a zip for worlds and folders, a masked copy for logs).</summary>
    internal string? SourcePath { get; set; }
    /// <summary>The source is a packed copy in the outgoing folder, deleted once it has been delivered.</summary>
    internal bool Packed { get; set; }
    internal CancellationTokenSource? Packing { get; set; }
    /// <summary>Stops this send from the moment it is handed over, while the file is still being hashed too.</summary>
    internal CancellationTokenSource? Sending { get; set; }
    internal Amber.Core.Activity.TaskHandle? Task { get; set; }

    public double Percent => Size > 0 ? Math.Clamp(Done * 100.0 / Size, 0, 100) : State is FileCardState.Received or FileCardState.Delivered ? 100 : 0;
    private int WholePercent => (int)Math.Floor(Percent);
    public bool IsMoving => State == FileCardState.Sending;
    public bool IsActive => FileCards.IsActive(State);
    public bool IsProgram => Kind == FileKind.Program;
    public string KindText => L.T(FileKinds.LabelKey(Kind));
    public string SizeText => FileOfferItem.Size(Size);
    public string WhoText => PeerName.Length == 0 ? "" : L.F(Outgoing ? "files.card.to" : "files.card.from", PeerName);
    /// <summary>"38 MB · World · To Alex".</summary>
    public string Meta => string.Join(" · ", new[] { State == FileCardState.Packing ? "" : SizeText, KindText, WhoText }.Where(s => s.Length > 0));

    /// <summary>The icon by kind (a world, a list, a picture, a log, a folder, a warning for programs, else a page).</summary>
    public string IconKey => Kind switch
    {
        FileKind.World => "Icon.World",
        FileKind.ModList => "Icon.Mods",
        FileKind.Screenshot => "Icon.Image",
        FileKind.Log => "Icon.Terminal",
        FileKind.Folder => "Icon.Folder",
        FileKind.Program => "Icon.Warning",
        _ => "Icon.FileText",
    };

    /// <summary>The line under the name: where the file is now.</summary>
    public string StatusLine
    {
        get
        {
            var key = FileCards.LineKey(State, Outgoing);
            return State switch
            {
                FileCardState.Waiting or FileCardState.Queued or FileCardState.Unreachable or FileCardState.NotAccepted when Outgoing => L.F(key, PeerName),
                FileCardState.Sending => string.Join(" · ", new[]
                {
                    L.F(key, WholePercent),
                    Route,
                    Speed is { } bps && bps > 0 ? FileOfferItem.Size((long)bps) + "/s" : "",
                }.Where(s => s.Length > 0)),
                FileCardState.Interrupted => L.F(key, WholePercent),
                FileCardState.Received => L.F(key, _owner?.ReceiveFolderLabel ?? ""),
                FileCardState.Failed => L.F(key, Error ?? ""),
                _ => L.T(key),
            };
        }
    }

    /// <summary>The program warning that cannot be closed, on a program someone sent.</summary>
    public bool ShowProgramWarning => IsProgram && !Outgoing;

    private FileCardActions Actions => FileCards.ActionsOf(State, Outgoing, Kind);
    public bool CanCancel => Actions.HasFlag(FileCardActions.Cancel);
    public bool CanResume => Actions.HasFlag(FileCardActions.Resume);
    public bool CanOpen => Actions.HasFlag(FileCardActions.OpenFolder) && !string.IsNullOrEmpty(SavedPath);
    public bool CanImportWorld => Actions.HasFlag(FileCardActions.ImportWorld);
    public bool CanCompareMods => Actions.HasFlag(FileCardActions.CompareMods);
    public bool CanSendAgain => Actions.HasFlag(FileCardActions.SendAgain);
    public bool HasCompare => CompareText.Length > 0;

    /// <summary>The instances under [Import into an instance] (a received world).</summary>
    public IReadOnlyList<CardTarget> ImportTargets => [.. (_owner?.InstanceChoices ?? []).Select(c => new CardTarget(this, c, import: true))];

    /// <summary>The instances under [Compare mods] (a received mod list).</summary>
    public IReadOnlyList<CardTarget> CompareTargets => [.. (_owner?.InstanceChoices ?? []).Select(c => new CardTarget(this, c, import: false))];

    /// <summary>The room's file list (one line per file).</summary>
    public string Text => L.F(Outgoing ? "multiplayer.private.sending" : "multiplayer.private.receiving", Name) + " · " + StatusLine;

    /// <summary>What a screen reader says for the card.</summary>
    public string Spoken => string.Join(" · ", new[] { Name, Meta, StatusLine }.Where(s => s.Length > 0));

    internal void Sample(long done, DateTimeOffset now)
    {
        if (_sampleTime != default && done > _sampleDone && now > _sampleTime)
        {
            var seconds = (now - _sampleTime).TotalSeconds;
            if (seconds >= 0.25)
            {
                var rate = (done - _sampleDone) / seconds;
                Speed = Speed is { } old ? old * 0.6 + rate * 0.4 : rate;
                _sampleTime = now;
                _sampleDone = done;
            }
        }
        else if (_sampleTime == default || done < _sampleDone)
        {
            _sampleTime = now;
            _sampleDone = done;
        }
        Done = done;
    }

    internal void ResetSpeed()
    {
        Speed = null;
        _sampleTime = default;
        _sampleDone = 0;
    }

    /// <summary>Everything on the card may have changed.</summary>
    internal void Changed() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private Task CancelAsync() => _owner?.CancelCardAsync(this) ?? System.Threading.Tasks.Task.CompletedTask;

    [RelayCommand]
    private void Resume() => _owner?.ResumeCard(this);

    [RelayCommand]
    private void SendAgain() => _owner?.ResumeCard(this);

    [RelayCommand]
    private void OpenFolder() => _owner?.OpenTransferFolderCommand.Execute(this);

    internal Task ImportWorldAsync(InstanceChoice instance) => _owner?.ImportReceivedWorldAsync(this, instance) ?? System.Threading.Tasks.Task.CompletedTask;

    internal Task CompareModsAsync(InstanceChoice instance) => _owner?.CompareReceivedModsAsync(this, instance) ?? System.Threading.Tasks.Task.CompletedTask;
}

/// <summary>One instance in a card's [Import into an instance] or [Compare mods] list.</summary>
public sealed partial class CardTarget(TransferItem card, InstanceChoice instance, bool import) : ObservableObject
{
    public string Name => instance.Name;

    [RelayCommand]
    private Task GoAsync() => import ? card.ImportWorldAsync(instance) : card.CompareModsAsync(instance);
}
