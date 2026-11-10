using System.Collections.Specialized;
using Amber.Core.Activity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Avalonia.Threading;

namespace Amber.App.ViewModels;

/// <summary>
/// Knocks and incoming files are answered in four places that stay in sync (official §6.15): the locked panel in the room,
/// the request bar on other pages, the topmost popup while Jay is in the background, and (later) a system notification.
/// The room's cards are the source of truth; each one is mirrored as a safety request, and whichever place answers first wins.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private readonly Dictionary<ApprovalCard, RequestHandle> _knockRequests = [];
    private readonly Dictionary<FileOfferItem, RequestHandle> _fileRequests = [];

    private void InitRequests()
    {
        ApprovalCards.CollectionChanged += (_, e) => Mirror(e, _knockRequests, KnockRequestSpec);
        FileOffers.CollectionChanged += (_, e) =>
        {
            Mirror(e, _fileRequests, FileRequestSpec);
            OnPropertyChanged(nameof(HasFileOffers));
        };
        _service.Requests.Resolved += handle =>
        {
            if (handle.ResolvedBy == RequestSource.Program || handle.Spec.Kind is not ("knock" or "file")) return;
            Dispatcher.UIThread.Post(() => AnswerFromElsewhere(handle));
        };
    }

    /// <summary>A card came or went: post its request, or take the request back when the card was answered here.</summary>
    private void Mirror<T>(NotifyCollectionChangedEventArgs e, Dictionary<T, RequestHandle> map, Func<T, RequestSpec> spec) where T : notnull
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var handle in map.Values) _service.Requests.Resolve(handle, "closed", RequestSource.Program);
            map.Clear();
            return;
        }
        foreach (var item in e.OldItems?.OfType<T>() ?? [])
            if (map.Remove(item, out var handle)) _service.Requests.Resolve(handle, "answered", RequestSource.Program);
        foreach (var item in e.NewItems?.OfType<T>() ?? [])
            map[item] = _service.Requests.Post(spec(item));
    }

    private RequestSpec KnockRequestSpec(ApprovalCard card) => new("knock", card.Headline,
        L.F("multiplayer.knock.request_body", card.SafetyCode, card.VerifyHint),
        [new("deny", L.T("multiplayer.invite.deny"), IsDefault: true), new("approve", L.T("multiplayer.invite.approve"))],
        IsSafety: true,
        Timeout: card.Deadline is { } d ? d - DateTimeOffset.Now : null,
        DedupeKey: "knock:" + Guid.NewGuid().ToString("N"),
        TimeoutChoice: "deny");

    private static RequestSpec FileRequestSpec(FileOfferItem offer) => new("file", offer.Headline,
        offer.IsProgram ? L.T("multiplayer.file.program_warning") : L.T("multiplayer.file.never_runs"),
        [new("decline", L.T("multiplayer.private.decline"), IsDefault: true), new("accept", L.T("multiplayer.file.accept"))],
        IsSafety: true,
        DedupeKey: "file:" + offer.Offer.Id);

    /// <summary>Answered on the request bar or the popup: do the same as the room's buttons.</summary>
    private void AnswerFromElsewhere(RequestHandle handle)
    {
        if (handle.Spec.Kind == "knock" && _knockRequests.FirstOrDefault(p => p.Value == handle).Key is { } card)
        {
            _knockRequests.Remove(card);
            if (handle.Choice == "approve") ApproveKnock(card);
            else DenyKnock(card);
        }
        else if (handle.Spec.Kind == "file" && _fileRequests.FirstOrDefault(p => p.Value == handle).Key is { } offer)
        {
            _fileRequests.Remove(offer);
            if (handle.Choice == "accept") AcceptFileCommand.Execute(offer);
            else DeclineFileCommand.Execute(offer);
        }
    }

    /// <summary>Settings › invites and alerts: the topmost popup while Jay is in the background.</summary>
    public bool RequestPopup
    {
        get => _service.Settings.MultiplayerRequestPopup;
        set
        {
            if (_service.Settings.MultiplayerRequestPopup == value) return;
            _service.Settings.MultiplayerRequestPopup = value;
            _service.Settings.Save();
            OnPropertyChanged();
        }
    }

    public bool HasFileOffers => FileOffers.Count > 0;

    /// <summary>The request mirroring a knock (self-check).</summary>
    internal RequestHandle? RequestFor(ApprovalCard card) => _knockRequests.GetValueOrDefault(card);

    /// <summary>The request mirroring a file offer (self-check).</summary>
    internal RequestHandle? RequestFor(FileOfferItem offer) => _fileRequests.GetValueOrDefault(offer);
}
