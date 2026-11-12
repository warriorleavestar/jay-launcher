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
| Sealed direct code `JAYDS1-` / reply `JAYDR1-` | Anywhere; the 16-digit passphrase by another way (call, in person) | Nothing without the passphrase: the addresses and the invite inside are encrypted. With both, the host's IP and a one-use, 10-minute invite the host still approves. |
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
2. **Root-certified**: the connection string lists root key ids (`dirRoots`, at most 4; each the SHA-256 id of a hybrid
   Ed25519 + ML-DSA-65 key, see `TrustSigner`) and how many of them must
   sign (`dirRootsNeed`, default 1). The built-in network uses the Jay trustees (`JayTrust`): 2 of 3.
   - A directory sends `{"c","sigs"}` as the payload of its handshake message.
   - `c` is `{"key","net","exp"}`; `sigs` is a list of base64 `TrustSignature` entries (both public keys, an Ed25519
     and an ML-DSA-65 signature over `"jay-directory-cert-v2\n" ‖ c`, about 5.3 KB each). Both algorithms must verify;
     one bad signature rejects the certificate.
   - Jay accepts the key if enough distinct listed roots signed, `net` is this network's EasyTier name, it hasn't
     expired, and `key` is the key the directory just proved.
3. **First use** (networks with neither): the first key seen at each directory address is remembered in
   `directory-keys.json`. After that, only that key is accepted for that address.
   `DirectoryKeyChangedException` tells the user to remove and re-add the network if the change was intended. There is no
   plaintext protocol to fall back to.

### Trustee keys for the built-in network (offline, one per person)

The built-in network's directory certificates, and reviewed network strings, need 2 of the 3 Jay trustees
(`src/Amber.Core/Multiplayer/Public/TrustQuorum.cs`). Each trustee makes their own key on their own offline machine:

```bash
jay-server network root-key --out <usb>/jay-trustee.key --paper
# asks for a passphrase (the file is sealed with Argon2id + ChaCha20-Poly1305), shows the two paper lines once,
# prints the full public key → it goes into JayTrust.Keys, then a release
jay-server network paper-check --key <usb>/jay-trustee.key   # check the hand-copied lines

# on each directory node: print its directory public key (makes directory.key on first use)
jay-server network dir-key --dir <node data dir>

# trustee 1, offline, with only that public key:
jay-server network dir-cert --root <usb>/jay-trustee.key --network jay-public-v1 --dir-key <node's key> --out directory.cert --days 365
# trustee 2, on their own offline machine, adds their signature to the same file:
jay-server network dir-cert --cosign directory.cert --root <usb>/jay-trustee.key
# copy directory.cert into the node's data dir; the node logs "directory certificate" at start
```

No trustee key goes on a server, CI, a cloud drive, a chat or the repository. Certificates are not secret. A new or
re-keyed node needs only a new certificate, not a Jay release. If one trustee loses their key, the other two can still
sign; replacing a trustee needs a release with the new public key.

## Direct links without an IP in any code (done)

A direct room hands out a plain `JAY2-` invite. The guest knocks over the lobby (relay, LAN or public network) as for any
room. Only when the host approves does the encrypted knock reply carry the host's addresses (`"direct"`, built from
`DirectAddresses`); the guest answers with its own inside the encrypted ack, tagged with the host's session id. Then both
sides punch at once, and the room falls back to the relays if that fails. Someone who copies the invite learns no address
of either side, and a rejected or expired knock gets none either. The bridge's control messages (authenticated, not
encrypted) carry only the session id. `JAY2D-` / `JAY2R-` are refused.

## Direct links with no server at all: sealed codes

For two people with no relay, no public network and no shared LAN. `Multiplayer/SealedDirect.cs`:

- The host's `JAYDS1-` code holds its `DirectAddresses` and a one-use JAY2 invite, encrypted. Jay makes up a
  16-digit passphrase (about 53 bits); the host tells it by another way. The key is Argon2id(passphrase, 16-byte salt
  in the code; 64 MiB, 3 passes), and HKDF-SHA256 splits it into an offer key, a reply key and a 4-byte key id. Both codes
  use ChaCha20-Poly1305 with the prefix (and salt or key id) as associated data.
- The friend opens it, gets a `JAYDR1-` reply sealed under the same passphrase (no second passphrase), and sends it
  back. Both sides punch; the friend knocks over the bridge, which reaches only the host's lobby, and the host approves
  as usual.
- Each code expires after 10 minutes (the time is inside the ciphertext); a code dated far ahead is refused.
- Limits, said in the UI: whoever holds both the code and the passphrase can read the IP, so they must not be pasted
  together. A stolen code is an offline guessing target for the passphrase; 53 bits behind 64 MiB of Argon2id per guess
  makes that impractical (centuries at a million guesses a second).

## Jay server invites: a domain or a sealed code

`Multiplayer/Gate/GateInvite.cs`. Rule: a code may name a server by its domain, never by a raw IP, and never names the player.

- `JAYS2-` (plain, Crockford Base32) carries the gate's domain and port, the gate key hash, the one-use token and the
  optional port rotation. The player's name is gone: the gate keeps which name an invite is for and returns it at
  enrollment. A plain code with an IP in it is refused when made and when pasted.
- A server with only an IP (a home server, a LAN test, a privacy-plus relay with no name) gets `JAYSS1-` instead: the same
  bytes sealed with a new 16-digit passphrase, the same construction as the sealed direct codes (Argon2id, HKDF info
  `jay-sealed-server-invite-v1`, ChaCha20-Poly1305, prefix + salt as associated data). The admin tells the passphrase
  another way. No expiry inside: the gate still expires and counts the token.
- Old `JAYS1-` codes (IP and player name in plain sight) are recognised and refused with "ask the admin for a new one".
- Diagnostics redact `JAYS1`, `JAYS2` and `JAYSS1` codes (they hold a token).

### IP-only servers without a passphrase: signed address records

`Gate/GateRecord.cs`, `Gate/GateRecordPublisher.cs`, turned on with `jay-server records on [--network …]`.

- The gate keeps an Ed25519 record key (`record.key`, 0600) and a random 16-byte locator id. Invites (`JAYS2-`
  version 3) then carry the network id, the locator id, a hash of the record key, the gate key hash and the token: no
  host, no port, no passphrase.
- While `jay-server run` runs, it joins that public network with its own EasyTier and puts a `SignedRecord` (ops
  `rec.put` / `rec.get`, #219) every 12 hours (3-day lifetime). Lookup and seal key come from the locator id, so a
  directory sees neither the id nor the address. The record lists the gate's own entry, or its relays in privacy-plus
  mode, with port rotation.
- At enrollment Jay joins the network briefly, gets the record, accepts it only when the signer matches the hash in
  the invite (someone who saw an invite can't redirect others), opens it and enrolls as usual; the gate key is still
  pinned by the handshake.
- The phone app can't join a public network yet and asks for a sealed invite instead. With records off, IP-only
  servers fall back to `JAYSS1-` sealed invites.

### Relay share codes: domains, a record locator or a sealed code

`Multiplayer/RelayNodes.cs`, `Multiplayer/Public/RelayRecord.cs`.

- Relays that all go by a domain are shared as plain `AMBNODE-` codes.
- Relays with an IP, while the sharer is on a public network: `JAYNR1-` carries the network id, a random 16-byte
  locator id and a hash of the sharer's record key (`relay-record.key` in the data folder, created once). The relay list
  is in a `SignedRecord` on that network's directories, put at share time and renewed on every connection (7-day
  lifetime). The reader must be on the same public network, accepts only a record signed by the key in the code, and
  adds the relays. Diagnostics redact `JAYNR1`.
- Not on a public network: `JAYNS1-`, sealed with a 16-digit passphrase (same construction as the server invites, HKDF
  info `jay-sealed-nodes-v1`).
- If the sharer stays off the network for over 7 days the record expires and the code stops resolving; sharing again
  renews it with the same code.

## Still to do

- **Relay enrollment the other way around**: a relay generates its own key and the admin approves its fingerprint, so
  JAYR1 codes carry no secret.
- **Jay server invites (JAYS2) bound to the player's identity key**, the same way room invites are.
- **Flood limits**: per-sender limits once directories can see a stable sender (for example, a cheap proof of work per
  post).

## One-shot rendezvous records

`Public/Rendezvous.cs`, directory ops `rv.put` / `rv.take` in `Public/RoomDirectory.cs`. These let a code carry a random token
instead of an address: the publisher puts a sealed record on the directories, the code's holder takes it once.

- **Derivation**: from a token of at least 16 random bytes, id = `HKDF-SHA256(token, info "jay-rv-id-v1")` (32 bytes) and
  seal key = `HKDF-SHA256(token, info "jay-rv-seal-v1")`. The record is ChaCha20-Poly1305 with a random nonce and the id as
  associated data. The directory sees only the id and the opaque record, never the token.
- **Single use**: `rv.take` removes the record in the same step that returns it. The client asks every directory at once,
  so taking the record also clears the other copies.
- **No overwrite**: `rv.put` on an id that is still waiting is refused with `exists`, so nobody can replace a record they
  didn't publish.
- **Encrypted channel**: like every directory op, only over `DirectoryChannel`.
- **Limits**: 2 KB per record, at most 10 minutes, 4096 records per directory, 30 puts per source address per 10 minutes.
  Nothing is written to disk. The node's `/metrics` shows only the number of waiting records (`jay_rendezvous_records`).

## Long-lived signed records

`Public/SignedRecord.cs`, directory ops `rec.put` / `rec.get` in `Public/RoomDirectory.cs`. A server or network that has no
domain name publishes where it is as a signed record, and its code carries an id instead of the address.

- **Derivation**: from an id of at least 16 bytes held by the code, lookup = `HKDF-SHA256(id, info "jay-rec-lookup-v1")`
  (32 bytes) and seal key = `HKDF-SHA256(id, info "jay-rec-seal-v1")`. The blob is ChaCha20-Poly1305 with a random nonce
  and the lookup as associated data, so the directory sees neither the id nor the contents.
- **Signature**: Ed25519 over `"jay-rec-v1" ‖ lookup ‖ signer ‖ seq ‖ expires ‖ len(blob) ‖ blob` (`RecordSigner`). The
  directory checks it, and the reader also checks the signer against what its code expects, so a directory can withhold a
  record but never substitute one.
- **Ownership**: the first signer owns a lookup until its record expires. A different signer gets `owner`; the same
  signer replaces it only with a higher seq (`stale` otherwise).
- **Lifetime**: at most 7 days per put, renewed by putting it again.
- **Encrypted channel**: like every directory op, only over `DirectoryChannel`.
- **Limits**: 4 KB per blob, 20000 records per directory, 60 puts per source address per hour, memory only. `/metrics`
  shows the total (`jay_signed_records`).

## Directory snapshot (privacy plan item 12)

`Public/DirectorySnapshot.cs` (`HiddenEntry`, `DirectorySnapshots`), directory op `snap` in `Public/RoomDirectory.cs`. Jay no
longer asks a directory "where is the room for this invite?". Every Jay downloads the whole directory and looks it up
locally, so the directory can't tell who looked for which room, or whether anyone looked at all.

- **Hidden entries**: for each open invite the host seals `{id, ip, key}` (padded to 256 bytes) with ChaCha20-Poly1305 under
  `HKDF-SHA256(lobbyKey, info "jay-dir-seal-v1")`. Every entry is 284 bytes.
- **Self-certifying index**: the invite also gives an Ed25519 entry key (seed `HKDF-SHA256(lobbyKey, info
  "jay-dir-entry-key-v1")`). The index is `SHA-256("jay-dir-index-v2" || entry public key)` (32 bytes, also the sealing
  associated data). A publish carries the entry public key `p` and a signature `s` over
  `"jay-dir-entry-v1\n" || index || sealed entry || room id || seq (int64 BE)`. The directory checks that the key hashes
  to the index and that the signature holds, so only holders of the invite can publish under its index: someone who
  only saw the index (say in an old snapshot, or after a directory restart) gets `lookup`. The signature covers the room
  and the listing version, and the version is the listing's time in milliseconds, which must be within 10 minutes of
  the directory's clock (`clock` otherwise), so a captured entry can't be replayed for another room or much later. The
  directory still never sees the lobby key, so it can't open an entry or tell which invite it belongs to.
- **Sharing an index**: up to 8 rooms (`MaxEntriesPerIndex`) may have an entry under the same index, each under a
  random slot tag; only holders of the invite can fill those places. A reader tries every entry under the index. When
  the invite names its host (the host key is in it), the entry for that host is the answer; otherwise, when entries
  that open point at different hosts, the reader trusts none of them.
  Renewals reuse the same sealed bytes while nothing changed, so they don't show up as changes.
- **Public rooms**: a renewal that only changes the seq, signature and time isn't a snapshot change; readers keep the
  earlier signed version, and expiry shows up as `gone` either way.
- **One request**: `snap` with the version of the copy Jay already has. The reply holds what changed since then (or
  everything, for a new copy or one from before the directory restarted), padded to a multiple of 64 KB.
- **Fixed schedule**: while the public network is connected, the app refreshes every 60 seconds whatever the user does.
  Lookups and the public list read the local copy and make no request. Without a poller (CLI, tests) a read refreshes
  first. A reply is checked through before any of it is applied. A directory's copy that hasn't been refreshed for
  2 × 60 s + the 90 s room lifetime (the directory went away) is dropped.
- **Directory limits**: the snapshot is capped below the 16 MB reply limit (new publishes that would pass it get
  `full`); replies are serialized once per version. Small replies (up to 256 KB, the usual delta) have 32 slots of
  their own; large ones have 8 slots and a start budget of 32 at once refilling at 4 per second (`busy` otherwise).
  A large reply must go out at no less than 128 KB/s (10 s plus size over that rate), and each frame within 10 s.
  6 requests per source address per minute (`rate`). Behind the EasyTier forward every request comes from loopback,
  so the per-source limit (and the same limit on `rv.put` / `rec.put`) does not apply there; the slot and budget limits
  do.
- **Reader side**: the first refresh is awaited only until it has been tried; if it failed (`busy`, `version`,
  unreachable) the reader gets that error at once, and the poller tries again after 2, 5 and 10 s before going back to
  every 60 s. A reader whose copies have all gone stale gets an error, not an empty answer.
- **Not signed by the directory**: the copy comes over the authenticated `DirectoryChannel`, which already proves it is
  this network's directory. The plan's separate snapshot signature only matters once snapshots are mirrored by others.
- **Still on the wire**: `find` (fingerprint codes). A fingerprint code can only be matched with the host's search key,
  so it moves to the local copy only when friend codes carry an account hash instead (plan items 7 and 14).
- **Removed**: ops `list` and `lookup`, and the `lookup` field of `announce` (now `hidden`).

