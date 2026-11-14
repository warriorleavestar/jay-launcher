# Node hardware binding

Status: implemented, opt-in per node, waiting for security review and a real-TPM test. Code: `src/Amber.NetworkServer/Hardware/`. Tests: `src/Amber.Core.Tests/NodeHardwareBindingTests.cs`.

## The problem

[Directory identity](directory-identity.md) proves that a directory holds `directory.key`. It doesn't say *which machine* holds it. Anyone who gets a copy of the node's data folder (a stolen disk image, a careless backup, a hosting provider's snapshot) can start the same node elsewhere and impersonate the network's directory, including its login key.

## What binding adds

A node operator can bind a node to the machine it runs on. All of it happens offline, and no root key ever touches the server.

1. **On the node**: `jay-server node bind` reads the machine's hardware identifiers:
   - the TPM endorsement key (Linux, through tpm2-tools)
   - the board UUID or serial, where it is readable without root
   - the system disk's serial
   - the OS machine id

   It writes `hardware.request.json` with the node's directory public key, a random 32-byte salt, and `HMAC-SHA256(salt, "jay-hw-id-v1" ‖ kind ‖ value)` for each identifier. Raw identifiers never leave the machine.
2. **Offline, by two of three trustees**: `jay-server network hw-cert --root <root key> --request hardware.request.json --out hardware.cert` makes the certificate with the first signature: a hybrid trustee signature (Ed25519 and ML-DSA-65, both over `"jay-hardware-cert-v1\n"` ‖ content, both must verify). A second trustee adds theirs on their own offline machine with `jay-server network hw-cert --cosign hardware.cert --root <their key>`. Key files may be passphrase-sealed; the command asks for the passphrase. The file holds `{"c": content, "sigs": [<base64 trustee signature>, …]}`, the same shape as directory certificates (PR #176).
   - The built-in network needs `JayTrust.Need` (2) of the `JayTrust.Keys` trustees. A network of your own needs `dirRootsNeed` of its `dirRoots`.
   - One bad or repeated signature rejects the whole certificate. Content fields this version doesn't know are rejected too, so nobody can vouch for something Jay would silently ignore.
   - Before signing, each command prints every field: directory key (hex), network, identifiers, `need`, `must`, TPM, Secure Boot and the existing signers. It also prints the first 8 bytes of the signed bytes' SHA-256 in 4-character groups, for the trustees to compare by phone. It then asks the signer to type the last 6 characters of the directory key (base64, as printed). There is no flag to skip this, and piped input is refused.

    The certificate says which directory key belongs on which identifiers, how many of them must match (`need`, default: all but one, at least two), and which ones are mandatory (`must`: the TPM when present). It can also require Secure Boot (`--secure-boot`). It has no expiry, because a node must not wipe itself just because a date passed.
3. **Back on the node**: `jay-server node bind --install hardware.cert [--network <string>]` refuses a certificate that doesn't meet the network's root quorum. It also checks that the certificate is for this node's key and that *every* identifier matches. It then encrypts `directory.key` and `login.key` into `*.key.hw` (AES-256-GCM) and overwrites and deletes the plaintext files.

### How the keys are encrypted

- A random 32-byte data key encrypts the key files.
- For every subset of exactly `need` identifiers that contains all the mandatory ones, the data key is wrapped under `HKDF(share₁ ‖ … ‖ shareₙ)` and stored in `hardware.wrap`. Each share is `HMAC-SHA256(salt, "jay-hw-wrap-v1" ‖ kind ‖ value)`. Shares are never written anywhere, and they differ from the public ids in the certificate. That is how one replaced part (a new disk, say) doesn't lock the node out, while a copy on other hardware can't rebuild the key.
- **With a TPM**: the certificate requires it, and a second random secret is sealed to the TPM. The final key is `HKDF(data key ‖ TPM secret)`.
  - On **Linux**, `tpm2_create` seals it under an owner-hierarchy primary with a policy on **PCR 7** (the Secure Boot state). Only this TPM can unseal it, and only while the machine boots the same way.
  - On **Windows**, a non-exportable RSA key in the Microsoft Platform Crypto Provider wraps it (OAEP). This one is not bound to PCR 7; the Secure Boot check below covers that instead.
- **Without a TPM**: only the identifiers protect the keys. That is much weaker, because someone who can read the identifiers on the real machine can rebuild the key. The CLI says so.

## Every start

`NodeHardwareGuard` runs in the `JayNetworkServer` constructor, before any key is read or any port opened. It only runs when the node is bound, meaning `hardware.cert`, `hardware.wrap` or a `*.key.hw` file exists. Deleting the certificate doesn't make a node unbound.

| Check | On failure |
|---|---|
| Runtime integrity: debugger/tracer attached (`TracerPid`, `IsDebuggerPresent`), code-injection variables (`LD_PRELOAD`, `LD_AUDIT`, `DYLD_INSERT_LIBRARIES`, `DOTNET_STARTUP_HOOKS`, profiler variables), program, its folder or the data dir writable by group/others, Secure Boot off when the certificate requires it | **Refuse to run**, exit 78. Nothing is deleted. |
| TPM refuses to unseal while its endorsement key still matches (boot state changed) | **Refuse to run**, exit 78. Nothing is deleted. |
| Certificate validly signed but short of the network's root quorum (e.g. the node was started with the wrong `--network`) | **Refuse to run**, exit 78. Nothing is deleted. |
| Certificate names a network (`hw-cert --network`, the network's id or EasyTier network name) that isn't this node's | **Refuse to run**, exit 78. Nothing is deleted. `node bind --install` refuses it too. |
| Certificate missing, or any signature on it invalid | **Wipe**, exit 77 |
| An identifier can't be read at all (a tool failed or timed out, the service user isn't in `tss`), and the ones that were read would pass on their own; or the certificate requires a TPM and none is usable | **Refuse to run**, exit 78. Nothing is deleted. |
| Fewer than `need` identifiers match, or a mandatory one (TPM) was read and doesn't match | **Wipe**, exit 77 |
| `hardware.wrap` or a `*.key.hw` file doesn't open, or the sealed directory key isn't the certified one | **Wipe**, exit 77 |

While the node runs, the runtime checks repeat every 10 seconds. If one fails, the node stops with exit 78.

Wrong hardware leads to a wipe. A tampered runtime or an identifier that can't be read only leads to a refusal, because those signals have innocent causes, such as an admin attaching a debugger, a system update changing the boot chain, or a missing tool. A copy on other hardware still can't open the sealed keys, because the identifiers it can't read can't rebuild the key either.

## The wipe

`NodeWiper` deletes only names it knows, and only inside a folder that carries the `.jay-node` marker, which every node writes into its data dir. It deletes:

- `directory.key`, `login.key` and their `.hw` copies
- `directory.cert`, `hardware.cert`, `hardware.wrap`, `hardware.request.json`
- `accounts.json`, `bans.json`, `admin.token`, `directory-keys.json`
- the `public-node/` folder (EasyTier state)

Guards:

- It refuses to run on a filesystem or drive root, a home, Windows or Program Files folder, anything less than two levels deep, a data dir that is itself a link, or a folder without the marker.
- Regular files are overwritten with random bytes and flushed before deletion. Links are removed, never followed.
- It appends one line to `wiped.log`: the time, how many items were removed, and why. No secrets go in it.
- It never touches the program, other files in the folder, or anything outside it.
- A wiped node stays down: while `wiped.log` exists the guard refuses to start, so the node can't quietly come back as a new, unbound identity. The systemd unit from `server/install-network-node.sh` doesn't restart on exit 77 or 78.
- `jay-server directory --wipe-dry-run` only logs what would be deleted (the node still refuses to start). `jay-server node bind --check` reports whether the machine currently passes, and never deletes anything.

Back up `directory.key` and `login.key` offline before `--install`. After a false positive, restore them into a fresh data dir and bind again.

## No root needed

The node runs as its own service user (`jay-network`). On Linux, add that user to the `tss` group for `/dev/tpmrm0`. The board UUID is root-only on most distributions, so it is simply left out. The TPM, the disk serial and the machine id are enough.

## What this does not cover

- **Root on the real machine.** Someone with root on the bound machine can run the node, or read its memory, there. A process can't reliably detect a full compromise of its own OS. PCR 7 sealing catches a changed boot chain (Secure Boot turned off, an unsigned bootloader or kernel), and the runtime checks catch the common user-space ways in, but a kernel-level attacker on a correctly booted machine can defeat both.
- **Patching the program.** Someone who copies the files can run a modified `jay-server` that skips the wipe. What stops them is that the keys don't open on other hardware. The wipe is an extra layer for unmodified copies.
- **Remote triggers.** There are none. The check runs only locally (CLAUDE.md forbids remote kill switches).
- **Clients.** Jay clients don't see the hardware certificate. They keep authenticating the directory by its key and `directory.cert`.
- **Windows boot-state binding.** On Windows the TPM key isn't bound to PCR 7. The registry Secure Boot flag is checked instead, which is weaker.
