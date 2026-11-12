# Device keys and Jay account certificates

This is privacy plan item 7. Each device has its own keys, and a Jay account vouches for them with signatures that hold up against a quantum computer.

## Device keys

`identity.key` stores a format byte (`0x02`) followed by a 32-byte random seed. The file is encrypted as before: DPAPI on Windows, and `identity.wrap` with AES-GCM elsewhere. The seed is never derived from the recovery phrase. Restoring an account on another computer therefore never recreates this device's keys.

HKDF-SHA256 over the seed gives three keys, one per job (`DeviceKeys`):

| Label | Key | Job |
|---|---|---|
| `jay-device-dh-v1` | X25519 | Handshakes. Hosts and contacts know the device by this public key. |
| `jay-device-sig-ed-v1` | Ed25519 | Device signatures |
| `jay-device-sig-mldsa-v1` | ML-DSA-65 seed | Device signatures |

A device signature (`HybridSignature`) is both signatures over the same bytes. It counts only when both verify. Every use has its own domain string.

An `identity.key` from before device seeds holds a bare X25519 key. It is replaced by a new seed once, with a warning, and hosts that knew the device see a new member. There is no compatibility path.

The device code (`JAYF1-`, `DeviceFingerprint`) stays a separate key because the user must be able to rotate it on its own.

## Jay account keys and address

Two account keys come from the phrase (SLIP-0010 with Jay's root key, all hardened):

| Path | Key |
|---|---|
| `m/0'/0'` | Ed25519 account key |
| `m/9'/0'` | ML-DSA-65 account key (HKDF-SHA256 over the node, info `jay-account-mldsa-v1`, gives the 32-byte seed) |

The account id is `SHA-256("jay-account-id-v1\n" ‖ Ed25519 public key ‖ ML-DSA-65 public key)`. The address is `jay1` + Bech32m(version 1 ‖ id). Version 0 addresses (the bare Ed25519 key) are no longer valid.

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

A proof copied into another session doesn't verify. A proof also holds if X25519 alone were broken, because the device's signing keys are named in the certificate. The knock frame limit is 32 KiB.

## Contacts

Add-contact mail carries no account. The other side learns which person a device belongs to from a live knock (`ContactBook.SeenLive`):

- A verified contact without an account is grouped under the proven account.
- A device the user doesn't have yet, certified by the account of someone already verified, joins that person as a new device waiting for the user's OK. This does not happen if the user removed that device, or if the certificate's epoch is older than one already seen from that account.

Relinking after a friend list restore is an ordinary add-contact request, and each friend compares the emoji once.
