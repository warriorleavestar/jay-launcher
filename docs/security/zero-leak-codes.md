# Codes that are useless when leaked

Status: implemented, including the UI (Multiplayer settings › Contacts, room header [Invite a contact], mailed invites in
the activity centre) and first-use directory keys in `directory-keys.json`. The remaining items below are follow-ups.

People paste Jay's codes into QQ, WeChat, Discord, forums and screenshots. Those platforms keep and scan what they carry,
and anyone else in the group can copy it. The goal: **nothing pasted on a third-party platform lets anyone else in**.
Codes that must travel are either public by design (a public key) or never leave the Jay network (sealed mail).

## What each piece gives an attacker who copies it

| Item | Where it travels | What a copy is good for |
| --- | --- | --- |
| Contact code `JAYC1-…` | Anywhere (public) | Sending that person mail they still have to accept. Not reading their mail, not posing as them. |
| Emoji check (4 emoji) | Voice, in person, video | Nothing; it is only compared. |
| Room invite sent by mail | Sealed mail through the directories | Nothing: sealed to one recipient, bound to that recipient's key, names the host's key. |
| Plain `JAY2-` invite | Still accepted (LAN, offline) | Knocking with a one-use token the host still has to approve. No IP, port or room secret in it. |
| `JAY2D-` / `JAY2R-` direct codes | Removed: refused with a hint to send a `JAY2-` invite | They carried both sides' public IPs. Direct rooms now hand out plain `JAY2-` invites. |

## Contact codes

`JAYC1-` + Crockford Base32 of the X25519 identity public key and a 2-byte check (55 characters).
`Contacts/ContactCode.cs`. Parsing ignores case, spaces and dashes, and refuses a code with a wrong check.

The JAYF1 device fingerprint code (shown in the UI as the "friend code") is a different thing: it finds a friend's hidden
room. The UI needs a name for JAYC1 that doesn't clash with it.

## Adding a contact: the emoji check

A platform that swaps contact codes could place itself in the middle. Both people compare four emoji to catch that.
Four emoji are 24 bits, too few to compare against fixed keys, since a swapper could search for a key that gives the same
emoji. So the emoji come from both keys and two fresh nonces, exchanged with a hash commitment (the ZRTP / Bluetooth
numeric comparison construction). `Contacts/ContactHandshake.cs`, `Contacts/ContactBook.cs`:

1. A → B `hello`: A's name and `SHA-256("jay-sas-commit-v1" ‖ nA)`.
2. B accepts. B → A `hello-r`: B's name and nB.
3. A → B `hello-n`: nA. B checks that nA opens the commitment and drops the exchange if it doesn't.
4. Both show `SHA-256("jay-sas-v1" ‖ pubA ‖ pubB ‖ nA ‖ nB)`, first 24 bits, as 4 of 64 emoji.

A party in the middle must fix its nonces before it sees the other side's, so a swap survives with probability 2^-24 per
attempt, and each attempt costs the two people a comparison. If both add each other at the same time, the larger key
yields and answers as if asked. Only verified contacts can be sent invites, and only invites from verified contacts are
shown.

## Sealed mail through the directories

`Contacts/SealedMail.cs`, `Contacts/ContactPost.cs`, directory ops `post` / `fetch` / `drop` in `Public/RoomDirectory.cs`.

- **Inner layer**: `PrivateSeal` from sender to recipient (static-static DH), which proves the sender.
- **Outer layer**: a fresh X25519 key to the recipient, HKDF-SHA256, AES-256-GCM. This hides the sender's key from the
  directory.
- **Mailbox id**: 16 bytes of `SHA-256("jay-mailbox-v1" ‖ pub ‖ ":" ‖ UTC day)`. It changes daily. The recipient checks
  yesterday, today and tomorrow.
- **Deleting**: each post carries `SHA-256("jay-mail-drop-v1" ‖ token)`. The token is inside the sealed mail, so only the
  recipient can delete it early. Otherwise mail expires after 48 hours.
- **Limits**: 4 KB per blob, 32 per mailbox, 8000 per directory.
- **Replays**: each message id is accepted once (`mail-seen.json`). Mail older than 48 hours, or dated more than 10 minutes
  ahead, is ignored.
- **Receipts**: an invite is answered with a sealed receipt. An invite with no receipt after 3 minutes is listed by
  `ContactPost.Unconfirmed()`, so the UI can say "may have been held back" and offer a direct link or the LAN.

Every post goes to every reachable directory, and fetches merge all of them. One honest directory is enough to deliver.

### What a directory, real or fake, can and can't do

- It **can't** read mail, tell who sent it, forge mail, or change it without the recipient noticing.
- It **can** drop or delay mail. Receipts reveal this, and other directories and the fallbacks route around it.
- It **can** see mailbox ids, blob sizes and timing. Someone holding a contact code can also work out that person's
  mailbox ids, so a directory operator who collects codes can tell when mail arrives for them. It still can't read the
  mail.
- It **can** fill mailboxes (flooding): 32 per box, 8000 in total. Senders can't be told apart behind EasyTier's
  forwarding, so there is no per-sender limit yet. Receipts surface the failure.

So **directory trust no longer decides whether a code leaks**. It only decides whether mail gets through.

## Invites bound to one person and to one host

`Knock.cs`:

- `InviteBook.Create(…, boundTo: contactKey)`: a knock with this invite from any other identity gets `denied`. The
  invite isn't used up and no failure is counted.
- `KnockClient.KnockAsync(…, expectedHost: hostKey)`: the guest stops right after the host's handshake message, before
  sending its name, device proof or account, if the host's proven key isn't the one named in the mailed invite. This is
  item 8 of the security report, for mailed invites.

## Directory trust: pinned keys, root certificates, first use

`Public/DirectoryTrust.cs`, `Public/DirectoryChannel.cs`. Jay picks one of these before sending a directory anything:

1. **Pinned**: keys in the signed connection string (`dirKeys`, #145).
2. **Root-certified**: the connection string lists Ed25519 root keys (`dirRoots`, at most 4). The built-in network uses
   `PublicNetworkProfile.JayDirectoryRoots`.
   - A directory sends `{"c","r","s"}` as the payload of its handshake message. Older Jay versions ignore it.
   - `c` is `{"key","net","exp"}` and `s` is the root's signature over `"jay-directory-cert-v1\n" ‖ c`.
   - Jay accepts the key if the root is listed, `net` is this network's EasyTier name, it hasn't expired, and `key` is
     the key the directory just proved.
3. **First use** (networks with neither): the first key seen at each directory address is remembered in
   `directory-keys.json`. After that, only that key is accepted, with no legacy fallback for that address.
   `DirectoryKeyChangedException` tells the user to remove and re-add the network if the change was intended. Until a key
   is remembered, a directory that only speaks the old protocol still works.

### Making the root key for the built-in network (project owner, offline)

```bash
jay-server network root-key --out <usb>/jay-directory-root.key
# prints the root public key → paste it into PublicNetworkProfile.JayDirectoryRoots and release

# on each directory node: print its directory public key (makes directory.key on first use)
jay-server network dir-key --dir <node data dir>

# on the offline machine, with only that public key:
jay-server network dir-cert --root <usb>/jay-directory-root.key --network jay-public-v1 --dir-key <node's key> --out directory.cert --days 365
# copy directory.cert into the node's data dir; the node logs "directory certificate" at start
```

Keep the root private key off servers, CI, cloud drives and the repository. Certificates are not secret. A new or
re-keyed node needs only a new certificate, not a Jay release. Rotating the root itself needs a release with both roots
listed for a while (up to 4).

## In the app

- **Contacts card** (Multiplayer settings): my contact code (shown as "contact code", unlike the JAYF1 "friend code"),
  a box for theirs, exchanges in progress and the contact list. A JAYC1 pasted anywhere (Ctrl+V, the join box) lands
  in this box; nothing is mailed until [Add].
- **Emoji card**: four large emoji, each with its localized name under it and as its accessible name
  (`multiplayer.contacts.emoji_names`, 64 names per language). [They match] saves the contact; [They don't match]
  drops the exchange and suggests sending the code another way.
- **Mail pick-up**: `ContactPost.CheckAsync` every 30 s while the public network is connected, and right after
  connecting. Add requests and ready-to-compare exchanges are mirrored as activity-centre requests.
- **Invite a contact** (room header, host only): a one-use, 30-minute invite bound to the contact's key and let in
  without asking, mailed with the host's key. Joining from the request knocks with `expectedHost`; a different host
  stops the knock before the guest says anything (`WrongHostException`, also for public-network and lobby knocks).
- **Held back**: no receipt after 3 minutes shows a banner in the room naming the other ways in: the invite dialog
  (direct link) or the game's LAN list when both are on the same network.

## Direct links without an IP in any code (done)

A direct room hands out a plain `JAY2-` invite. The guest knocks over the lobby (relay, LAN or public network) as for any
room. Only when the host approves does the encrypted knock reply carry the host's addresses (`"direct"`, built from
`DirectAddresses`); the guest answers with its own inside the encrypted ack, tagged with the host's session id. Then both
sides punch at once, and the room falls back to the relays if that fails. Someone who copies the invite learns no address
of either side, and a rejected or expired knock gets none either. The bridge's control messages (authenticated, not
encrypted) carry only the session id. `JAY2D-` / `JAY2R-` are refused.

## Still to do

- **Server and network codes without an address**: `JAYS1-` (Jay server), `JAYNET` (public network) and `AMBNODE`
  (relay share) still name a host or IP. Plan: a short token or ID resolved through a signed directory entry or the
  directory rendezvous, so the code itself names nothing.

- **Relay enrollment the other way around**: a relay generates its own key and the admin approves its fingerprint, so
  JAYR1 codes carry no secret.
- **Jay server invites (JAYS1) bound to the player's identity key**, the same way room invites are.
- **Flood limits**: per-sender limits once directories can see a stable sender (for example, a cheap proof of work per
  post).
