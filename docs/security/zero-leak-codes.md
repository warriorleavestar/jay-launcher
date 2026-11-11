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
   `directory-keys.json`. After that, only that key is accepted, with no legacy fallback for that address.
   `DirectoryKeyChangedException` tells the user to remove and re-add the network if the change was intended. Until a key
   is remembered, a directory that only speaks the old protocol still works.

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

## Still to do

- **IP-only servers without a passphrase**: an id + token in the code, the address resolved through the directory's
  signed-record store (`rec.put` / `rec.get`); the sealed code stays the fallback when no directory is reachable.
- **Network and relay codes without an address**: `JAYNET` (public network) and `AMBNODE` (relay share) still name a
  host or IP. Domain names stay allowed; IP-only nodes go the same way as servers.

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
- **Authenticated channel only**: both ops are answered only over `DirectoryChannel`; a legacy JSON request gets `secure`.
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
- **Authenticated channel only**: a legacy JSON request gets `secure`.
- **Limits**: 4 KB per blob, 20000 records per directory, 60 puts per source address per hour, memory only. `/metrics`
  shows the total (`jay_signed_records`).
