# Device keys and Jay account certificates

This is privacy plan item 7. Each device has its own keys, and a Jay account vouches for them with signatures that hold up against a quantum computer.

## Device keys

`device.key` stores a format byte (`0x02`) followed by a 32-byte random seed. The file is encrypted as before: DPAPI on Windows, and `identity.wrap` with AES-GCM elsewhere. The seed is never derived from the recovery phrase. Restoring an account on another computer therefore never recreates this device's keys.

HKDF-SHA256 over the seed gives three keys, one per job (`DeviceKeys`):

| Label | Key | Job |
|---|---|---|
| `jay-device-dh-v1` | X25519 | Handshakes. Hosts and contacts know the device by this public key. |
| `jay-device-sig-ed-v1` | Ed25519 | Device signatures |
| `jay-device-sig-mldsa-v1` | ML-DSA-65 seed | Device signatures |

A device signature (`HybridSignature`) is both signatures over the same bytes. It counts only when both verify. Every use has its own domain string. With BouncyCastle (no system ML-DSA), the seed copies Jay makes are zeroed after use, but the keys BouncyCastle expands from them live in managed memory it doesn't let Jay clear.

Versions before device seeds keep a bare X25519 key in `identity.key`. When `device.key` doesn't exist yet, that key is kept as the handshake key, so hosts and friends still know the device, and a seed is added for the two signing keys. `device.key` then stores `0x03` ‖ the X25519 private key ‖ the seed. Resetting the identity gives a seed-only device like any new one.

If `device.key` is damaged and `identity.key` is still there, the handshake key is taken from `identity.key` again with new signing keys, so hosts and friends keep knowing the device.

`identity.key` is only read, never written or moved. An older version that shares the data folder, or that the user goes back to, still finds its own identity and can't overwrite the new file. This also means that after resetting the identity, the old X25519 key stays in `identity.key`, and an older version would use it again. To retire it everywhere, delete `identity.key` by hand after the reset; Jay doesn't, because older versions may still need it. A reset also writes `identity.retired`, so this version never takes the old key back from `identity.key`, even when `device.key` is damaged or missing; the reset hint in the app says the same.

The device code (`JAYF2-`, `DeviceFingerprint`) stays a separate key because the user must be able to rotate it on its own.

## Jay account keys and address

Two account keys come from the phrase (SLIP-0010 with Jay's root key, all hardened):

| Path | Key |
|---|---|
| `m/0'/0'` | Ed25519 account key |
| `m/9'/0'` | ML-DSA-65 account key (HKDF-SHA256 over the node, info `jay-account-mldsa-v1`, gives the 32-byte seed) |

The account id is `SHA-256("jay-account-id-v1\n" ‖ Ed25519 public key ‖ ML-DSA-65 public key)`. The address is `jay1` + Bech32m(version 1 ‖ id).

Version 0 addresses (the bare Ed25519 key) are no longer valid. Migration works like this:

- Saved accounts move from `jay-accounts.json` to `jay-accounts-2.json` the first time the store loads without the new file. Each moves to its new address when its seed matches the old address. The old address is kept with the entry (`was`), so Minecraft account bindings follow on any later start too. An entry whose seed can't be opened yet is kept unchanged in the new file and tried again next time.
- `jay-accounts.json` stays for older versions and isn't read again. The only change made to it: removing an account takes its entry out of that file too, so no copy of the seed stays behind.
- Other people's version 0 addresses in contacts and trust lists are dropped, and learned again from their next knock.
- Friend backups from before this change still open: the old AAD binding and old address are accepted for the same account.

## Device certificate v2

`DeviceCertificate` says "these device keys belong to this account". It carries:

- both account public keys (the address is their hash);
- all three device public keys;
- the device name, epoch and issue time;
- both account signatures over all of the above, with domain `jay-device-cert-v2\n`.

It is about 7.4 KB. Version 1 certificates (one Ed25519 signature) are not read.

## Where certificates travel

Certificates travel only in live handshakes, never in mail. Sealed mail is 4 KiB, which can't hold a certificate.

In a knock, each side sends a `JayProof`: the certificate plus a fresh device signature (domain `jay-device-live-v1\n`) over the knock context. The context is a role label plus the Noise handshake hash:

- The guest's proof is inside the last handshake message, so it covers the hash before that message.
- The host's proof is in the encrypted reply, so it covers the finished handshake.

A proof copied into another session doesn't verify. Once an account is known, the step from that account to each of its devices is signed with both account keys and checked with both device signing keys, so it holds even if X25519 alone were broken.

The first link between a contact and an account is weaker. It rests on the contact's X25519 key and the emoji check, so it counts only after the user confirms it (see below).

A proof is about 14.5 KB of JSON, and the knock frame limit is 32 KiB. Older versions read at most 8 KB, so proofs are negotiated:

- The host's hello sets `jay2`, and the guest sends its proof only then.
- The guest's about sets `jay2`, and the host sends its own proof only then.
- Proofs travel in a `proof` field. The old `jay` field is no longer sent or read, so mixing versions just leaves the account out.

## Contacts

Add-contact mail carries no account. The other side learns which person a device belongs to from a live knock (`ContactBook.SeenLive`):

- A verified contact without an account gets the proven account, waiting for the user's OK. Until the user confirms it, the account vouches for nothing. If the user says no, the link is dropped and the same account isn't offered again for that device.
- A device the user doesn't have yet, certified by a confirmed account of someone already verified, joins that person as a new device waiting for the user's OK. This does not happen if the user removed that device, or if the certificate's epoch is older than any already seen from that account (devices still waiting for an OK count too).
- A contact saved with a version 0 account address forgets it, and learns the account again from the next knock.

Relinking after a friend list restore is an ordinary add-contact request, and each friend compares the emoji once.
