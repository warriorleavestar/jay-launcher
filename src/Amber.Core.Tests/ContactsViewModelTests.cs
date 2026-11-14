using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>Contacts in the multiplayer page (zero-leak codes): the emoji check, requests, mailed invites, held-back mail.</summary>
public sealed class ContactsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-contacts-vm-" + Guid.NewGuid().ToString("N"));

    public ContactsViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private MultiplayerViewModel NewVm(LauncherService service)
    {
        var vm = new MultiplayerViewModel(service) { ContactsDir = _root };
        return vm;
    }

    [Fact]
    public void AddingSomeoneShowsTheEmojiAndConfirmingSavesThem() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = NewVm(service);
        var me = vm.Identity.PublicKey;
        using var peer = X25519KeyPair.Generate();
        var peerBook = new ContactBook(Path.Combine(_root, "peer.json")) { Me = peer.PublicKey };

        // I add them; they accept; their reply gives me the emoji.
        var hello = vm.Contacts.StartAdd(me, peer.PublicKey, "Me")!;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Assert.Single(vm.ContactRequests).CanCancel);
        Assert.Null(peerBook.Receive(me, hello, "Peer"));
        var reply = peerBook.Accept(me, "Peer")!;
        var nonce = vm.Contacts.Receive(peer.PublicKey, reply, "Me")!;
        Assert.Null(peerBook.Receive(me, nonce, "Peer"));
        Dispatcher.UIThread.RunJobs();

        var item = Assert.Single(vm.ContactRequests);
        Assert.True(item.IsComparing);
        Assert.Equal(4, item.Emoji.Count);
        Assert.All(item.Emoji, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
        Assert.Equal(item.Emoji.Select(e => e.Emoji), peerBook.Pending.Single().Sas!.Select(i => ContactHandshake.Emoji[i]));
        Assert.Contains(service.Requests.Pending, r => r.Spec.Kind == "contact-compare");

        vm.ConfirmContactCommand.Execute(item);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.ContactRequests);
        Assert.Equal("Peer", Assert.Single(vm.ContactList).Name);
        Assert.DoesNotContain(service.Requests.Pending, r => r.Spec.Kind == "contact-compare");
        Assert.False(vm.CanInviteContacts); // not hosting
    });

    [Fact]
    public void ARequestToAddMeCanBeIgnoredFromTheActivityCentre() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = NewVm(service);
        using var peer = X25519KeyPair.Generate();
        var peerBook = new ContactBook(Path.Combine(_root, "peer.json")) { Me = peer.PublicKey };
        var hello = peerBook.StartAdd(peer.PublicKey, vm.Identity.PublicKey, "Alex")!;
        vm.Contacts.Receive(peer.PublicKey, hello, "Me");
        Dispatcher.UIThread.RunJobs();

        Assert.True(Assert.Single(vm.ContactRequests).IsAskingMe);
        var request = service.Requests.Pending.Single(r => r.Spec.Kind == "contact");
        Assert.Contains("Alex", request.Spec.Title);
        Assert.Equal("ignore", request.Spec.Choices.Single(c => c.IsDefault).Id);
        service.Requests.Resolve(request, "ignore", RequestSource.Bar);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.ContactRequests);
    });

    [Fact]
    public void APastedFriendCodeGoesToTheAddPanelOnTheFriendsPage() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = NewVm(service);
        using var peer = X25519KeyPair.Generate();
        var code = ContactCode.Format(peer.PublicKey);
        vm.JoinCode = code;
        Assert.Equal(L.T("multiplayer.lobby.code.contact"), vm.JoinCodeRecognition);
        vm.JoinCode = "";
        var routes = new List<string>();
        vm.Navigate = routes.Add;
        Assert.True(vm.PasteCode(code));
        Assert.Equal(["people/add"], routes);
        Assert.Equal(code, vm.ContactCodeInput);

        // Not connected to a public network: nothing is started, the code stays in the box.
        vm.AddContactCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.ContactRequests);
        Assert.Equal(code, vm.ContactCodeInput);
    });

    [Fact]
    public void AServerInvitePastedElsewhereGoesToTheLobbysPasteBox() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = NewVm(service);
        var code = Amber.Core.Multiplayer.Gate.GateInvite.SealedPrefix + new string('A', 64);
        Assert.Equal(PastedCodeKind.JayServer, PastedCode.Detect(code));

        // In the friend-code box: it moves to the lobby's paste box, which asks for the passphrase there.
        vm.ContactCodeInput = code;
        vm.AddContactCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", vm.ContactCodeInput);
        Assert.Equal(MultiplayerTab.Play, vm.Tab);
        Assert.Equal(code, vm.JoinCode);
        Assert.True(vm.JoinNeedsPassphrase);
        Assert.Equal("core.gate.invite.sealed", vm.StatusInfo?.Key);
        Assert.Equal("", vm.JayServers.InviteText);

        // From the command palette: the same place.
        vm.JoinCode = "";
        vm.SetStatus(StatusMessage.Info("common.raw", "x"));
        vm.FillServerInvite(code, join: false);
        Assert.Equal(code, vm.JoinCode);
        Assert.Equal("core.gate.invite.sealed", vm.StatusInfo?.Key);
        Assert.Equal("", vm.JayServers.InviteText);
    });

    [Fact]
    public void MailedInvitesGoToTheActivityCentreAndMissingReceiptsWarn() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = NewVm(service);
        using var host = X25519KeyPair.Generate();
        var from = new Contact(host.PublicKey, "Sam", true, DateTimeOffset.UtcNow);
        var invite = new InviteBook().Create(1, TimeSpan.FromMinutes(30), true, TunnelSettings.Default, boundTo: vm.Identity.PublicKey).Invite;
        vm.ShowMailedInvites([new MailedInvite(from, invite, host.PublicKey, "Sam's world", DateTimeOffset.UtcNow.AddMinutes(30))]);
        var request = service.Requests.Pending.Single(r => r.Spec.Kind == "contact-invite");
        Assert.Contains("Sam", request.Spec.Title);
        Assert.Contains("Sam's world", request.Spec.Title);
        Assert.Equal("ignore", request.Spec.TimeoutChoice);
        Assert.NotNull(request.Deadline);
        service.Requests.Resolve(request, "ignore", RequestSource.Bar);

        vm.SentInviteForTest("abc", "Robin");
        vm.ShowHeldBack([new SentMail(host.PublicKey, "zzz", MailKind.Invite, DateTimeOffset.UtcNow)]);
        Assert.False(vm.HasHeldBack);
        vm.ShowHeldBack([new SentMail(host.PublicKey, "abc", MailKind.Invite, DateTimeOffset.UtcNow)]);
        Assert.True(vm.HasHeldBack);
        Assert.Contains("Robin", vm.HeldBackText);
        vm.DismissHeldBackCommand.Execute(null);
        Assert.False(vm.HasHeldBack);
    });

    [Fact]
    public void EveryLanguageNamesAllSixtyFourEmoji()
    {
        foreach (var language in L.Languages)
        {
            var names = L.Load(language.Code)["multiplayer.contacts.emoji_names"].Split('|');
            Assert.Equal(ContactHandshake.Emoji.Length, names.Length);
            Assert.Equal(names.Length, names.Select(n => n.Trim()).Distinct().Count());
        }
    }
}
