# Multi-path split for room connections

Status: stage 2 of 4. The transport (stage 1) and the room wiring (stage 2) are in. Nothing splits yet in practice: hosts don't turn it on, and members don't have the extra paths until stage 3 adds the EasyTier instances.

## What it is for

In an invite room, every member connection to the host (game tunnel, chat) is already end-to-end encrypted and authenticated with that member's own psk, a hybrid Noise handshake (X25519 + ML-KEM-768) and, when the room uses it, device attestation. The split adds one more layer: the connection runs over 2 or 3 network paths at once, for example one direct path and one through a relay node, and each path carries only one share of every chunk.

- Anyone who sees fewer than all paths, such as a relay operator, or someone recording one path now in the hope of breaking its crypto later, gets random-looking shares and nothing else.
- It does **not** hide who talks to whom: relays still see addresses, and the users' own ISPs sit on every path.
- Two relay nodes can only be told apart by name, address range or an operator label. Jay can't prove that they are run by different people.

## Shares

Each chunk `d` (at most 16 KiB) becomes `n` shares. Legs `0 … n-2` carry fresh random pads `r_i`; the last leg carries `d ⊕ r_0 ⊕ … ⊕ r_(n-2)`. Every share is sent as `[u32 sequence][u16 length][bytes]` inside that leg's `SecureStream`. Pads come from `RandomNumberGenerator` and are zeroed after use.

The split adds no MAC of its own, because each leg is already authenticated end to end. The receiver checks that the chunk header is identical on every leg and that the sequence is the next one expected. Anything else counts as tampering and drops every leg. With two legs, the wire format is the same one the Jay server's privacy+ mode already uses (`Transport/SplitStream.cs` serves both).

## Legs and pairing

- Each leg is a full `MemberChannel` handshake. Its prologue is `jay-member-split-v1|purpose|mode|disguise|epoch|count|index|` followed by the member id, and its psk is `HKDF-SHA256(memberPsk, salt "jay-split-v1", info "path|<index>")`. A leg sent to the wrong port, with a different leg count or with credentials from an older key epoch fails the handshake. The host takes the index from the listening port, and the count and epoch from the room.
- After the handshake, the member sends a hello inside the encrypted leg: `[u8 version = 1][32-byte pairing id]`. The pairing id is random and identical on every leg of one connection.
- `SplitRegistry` (host) joins legs only when all of these hold: they authenticated as the same member entry, they carry the same leg count and service, and they fill distinct indexes. A leg that breaks any rule drops the whole set; the registry never picks a winner. A set waits at most 10 s. Each member may have at most 4 sets waiting, and the room at most 64. Used pairing ids are remembered for 10 s, so a replayed or late leg is dropped at once. A kick drops a member's waiting legs.

## Rooms

- **Settings.** The host decides per service: game and chat are each Off, Auto or Strict, over 2 or 3 paths. Remote control and the "game only" exit proxy are never split. Splitting needs the wrapped tunnel, so general mode never splits, and full-LAN (TUN) mode doesn't split in this version.
- **Credentials.** The room credentials carry `"split": {"game", "chat", "paths", "nets": [{"net", "secret"}]}`, one extra EasyTier network per extra path, random like the room's own network. They hold no address: which nodes a path uses is up to each member's own node list. A version that doesn't understand the field, or finds it malformed, connects on one path.
- **Rekey.** After a kick the host replaces every extra network along with the room's own (`"split": {"nets": [...]}` in the rekey message). A rekey without them turns splitting off, because the old networks are known to whoever was just kicked.
- **Host.** Leg *i* listens on port 47657 + *i* (47657, 47658, 47659). The handshake payload names the service, as on the single-path port 47656. A Strict service is not served on 47656 at all. If a leg port is taken by another program, an Auto room starts without splitting and logs why, and a Strict room doesn't start. A host in full-LAN (TUN) mode runs no split listeners in this version, so it refuses to start a room with a Strict service and says why; with Auto it serves everything on one path. A member in full-LAN mode doesn't split either; Auto uses one path and Strict fails, both with that reason. A member that can't read the split settings (an old version) still joins on one path, and a Strict host closes that connection and logs the refusal.
- **Member.** Each game or chat connection is opened by `SplitConnector`:
  - Auto splits when every leg opens. Otherwise it uses one path and records why.
  - After 3 failed split attempts in a row, Auto uses one path for 30 seconds before trying again, instead of retrying on every connection.
  - Strict splits or fails with the reason.
  - A device attestation failure never leads to a fallback.

## Ending

The joined stream ends cleanly only when every leg delivers its authenticated close record at the same chunk boundary. The following all drop every leg, and none of them is a clean end:

- one leg ending, failing or going silent for 15 s while another delivers;
- a header mismatch;
- the caller cancelling.

There is never a quiet fallback to fewer legs in the middle of a connection.

## Tests

`src/Amber.Core.Tests/SplitRoomTests.cs` covers the room side:

- settings, credentials and rekey round trips, and anything not understood meaning no split;
- game and chat end to end with 2 and 3 paths, and with the Minecraft disguise;
- Off, Auto and Strict with and without paths, a dead path, and Auto pausing after repeated failures and trying again later;
- a kick dropping a joined connection, remote control legs refused, and legs from an older epoch failing.

`src/Amber.Core.Tests/SplitTransportTests.cs` covers:

- shares recombining, no single share equal to the data, and pads differing between chunks;
- round trips over loopback with 2 and 3 legs;
- handshakes bound to the leg's index, count and epoch;
- refusing legs from two members, duplicate indexes and late legs;
- timeouts, the per-member limit, and kicks while waiting and after joining;
- tampering, one leg ending, a silent leg, and cancellation.

The full design, including path choice and UI, is in the project notes (mp-split-design v3).
