# Directory identity

Status: implemented and waiting for security review. It is opt-in per network and nothing is pinned yet for the built-in Jay network. Code: `src/Amber.Core/Multiplayer/Public/DirectoryChannel.cs`. Tests: `src/Amber.Core.Tests/DirectoryIdentityTests.cs`.

## The problem

A public network is an EasyTier network whose name and secret are public, so anyone can join it. Its room directory sits at a fixed address: `10.235.0.1`, with backups at `10.235.255.254`, `.253` and `.252`, on TCP port 47668. Jay sends every directory request to those addresses:

- `list` and `find` to browse public rooms
- `announce` and `withdraw` to publish your own room
- `lookup` to turn an invite code's lobby into the host's knock address
- `login`, `mail` and `x-*` extensions

The 2026-11-10 real-device test (finding 8) showed that EasyTier's DHCP handed the address `10.235.0.1` to an ordinary member while the directory was offline. Any member can also take the address on purpose with `--ipv4 10.235.0.1`. A machine holding the address can:

- read every announce: room names, host fingerprints, lookup keys and knock addresses. Hidden rooms only publish lookup keys, but the impostor still learns which keys are looked up and when;
- answer `list`, `find` and `lookup` with rooms it made up, which sends players to a knock port of the impostor's choosing (the knock handshake and the room's signed listing still stop impersonation of a specific host, but not phishing with fake rooms);
- see login requests (the password is sealed to the network's login key, so it isn't exposed) and mail;
- drop everything (denial of service).

The address proves nothing, so Jay needs to know it is talking to the directory before it sends anything.

## The fix

A network can pin its directory nodes' keys in its signed connection string. When it has, Jay checks that the other end holds one of those keys before it sends anything.

### Keys and where they come from

- Each directory node has an X25519 key pair in `directory.key` in its data dir. The node makes it on first start, and the file is created mode 0600 on Unix. `jay-server directory` prints the public key at startup, and `/health` reports it as `directoryKey`.
- `jay-server network dir-key <network.json> --dir <node data dir>` adds the node's public key to the network JSON's `"dirKeys"` array and bumps `seq`. The operator then re-signs the network (`jay-server network sign`).
- `NetworkInfo.DirectoryKeys` is base64 in JSON. You can pin 1 to 4 keys, one per directory node, and each must be 32 bytes. `NetworkString.Validate` enforces both limits. The connection string's Ed25519 signature covers the raw JSON bytes, so the keys are as trustworthy as the string: the registry's signature for reviewed networks, or the user's own confirmation for unreviewed ones.
- Older Jay versions ignore unknown fields, so adding `dirKeys` doesn't break them. They keep using the unauthenticated protocol (see Downgrade below).
- `PublicNetworkProfile.DirectoryKeys` carries the keys to `RoomDirectoryClient`, through `IPublicNetwork.Profile`.

### Protocol

Each connection carries one request, using `Noise_XXpsk0_25519_ChaChaPoly_SHA256`. This is the same Noise implementation (`Transport/Noise.cs`) that the knock and the room gate use.

| Step | Direction | Content |
|---|---|---|
| 1 | Jay → directory | `psk, e` with an empty payload (48 bytes) |
| 2 | directory → Jay | `e, ee, s, es` with an empty payload. Jay decrypts the directory's static key `s` and checks the tag, which proves the directory holds the private half of `s` (the `es` DH). Jay compares `s` in constant time against every pinned key. If none matches, Jay throws `DirectoryImpostorException` and closes the connection. Message 3 is never written, so the request never leaves the machine. |
| 3 | Jay → directory | `s, se`, with the JSON request as payload. Jay's static key is a throwaway generated for this connection, so the directory learns nothing that links two requests. |
| 4 | directory → Jay | One ChaCha20-Poly1305 record: the JSON reply under the responder's split key, with nonce 0 and AD `"jay-directory-v1"`. |

Details:

- **Prologue.** `"jay-directory-v1"`.
- **Psk.** `HKDF-SHA256(ikm = network EasyTier secret, salt = prologue, info = "jay-directory-psk-v1")`. The secret is public for public networks, so the psk only keeps handshakes of different networks apart. It is not an authentication factor; the pinned static key is.
- **Framing.** Every message is a 2-byte big-endian length followed by the bytes. The maximum is `RoomDirectoryProtocol.MaxLine + 256`, and the request and reply JSON are each capped at `MaxLine`.
- **Telling the protocols apart.** A legacy request is a JSON line that starts with `{` (0x7B). A Noise frame starts with the high byte of its length, which is 0x00 for every allowed size. The server reads one byte and dispatches on it (`RoomDirectoryServer.HandleAsync`).
- **Server failures.** Any handshake or decrypt failure on the server just closes the connection. The existing 64-slot concurrency cap and the request timeout apply to both protocols.

### Downgrade

- **Pinned clients.** A client whose network pins keys never falls back to the legacy protocol. A directory that answers only JSON, or that closes the connection, counts as unreachable for that address. `DirectoryImpostorException` is an `IOException`, so fan-out operations try the other directories. If every directory fails, the user sees "couldn't prove it is this network's directory".
- **Old clients.** Clients without pins, meaning Jay versions before this change or networks without `dirKeys`, keep the old unauthenticated behaviour.
- **Strict servers.** A server started with `--secure-directory-only` (`JayNetworkServerBuilder.RequireDirectoryChannel()`) refuses legacy requests. An operator who wants every client protected sets it together with `policy.minVersion`, so older Jay versions are told to update instead of failing silently.

### Rotation and loss

- **Rotating a key.** Add the new key to `dirKeys` while the old one is still listed, re-sign with a higher `seq`, wait until players have the new string, then remove the old key. With four slots there is room for one spare per node.
- **Losing `directory.key`.** Every Jay that pinned the old key will treat the replacement node as an impostor. That is intended. Operators must back the file up, and `dir-key` prints a reminder. Recovery is a re-signed string.

## What this does not cover

- **The built-in Jay network.** `PublicNetworkProfile.JayNodes` is still empty, so nothing is pinned yet. When the author deploys the official nodes, their keys have to be compiled in (`PublicNetworkProfile.Jay()` would set `DirectoryKeys`).
- **Unpinned networks.** Networks without `dirKeys`, and Jay clients older than this change, are unprotected. PR #137 also adds a client guard, `PublicNetwork.IsMemberAddress`, which stops counting a directory address as "connected" when the peer at it is an ordinary member. That is a heuristic, not authentication.
- **Denial of service.** A member who takes the address can still drop traffic. Backups at other addresses reduce the impact.
- **A malicious real directory.** The directory is still trusted with everything it legitimately sees. Room listings stay signed by hosts, and the knock handshake still authenticates hosts end to end.
- **Traffic analysis.** Someone inside EasyTier, such as a relay node, sees that a member talked to the directory, and roughly how much.

## Questions for the reviewer

1. Is `XXpsk0` with a throwaway initiator static the right pattern, or should this be `NKpsk0` (the responder's static key known in advance)? XX was chosen to reuse the reviewed implementation and the knock code path. It costs one extra DH per side and an extra 48 bytes in message 3. NK would send the request one round trip earlier, but it would need a new pattern in `Noise.cs`.
2. Jay checks the pin after message 2 and before message 3. Is there any information in message 1 (an ephemeral key only) that matters to an impostor?
3. The psk is derived from a public secret. Does mixing it in weaken anything compared with no psk?
4. Should the reply AD include anything besides the prologue, such as the request's op?
5. Is a 0x00 or 0x01 first byte safe as a discriminator against a legacy client that sends leading whitespace? Current clients never do.
