# Release signatures

A Jay release counts only when **2 of the 3 Jay trustees** have signed it, each with both **Ed25519 and
ML-DSA-65** (privacy plan, items 2 and 3). Every trustee holds **two separate keys**: a trust key for
networks, directory certificates and hardware certificates (`JayTrust.Keys`), and a **release key** used only
for releases and trustee list changes (`JayTrust.ReleaseKeys`, both in
`src/Amber.Core/Multiplayer/Public/TrustQuorum.cs`). `jay-server` refuses a release key for network signing and
a trust key for release signing, so losing or misusing one doesn't touch the other job.

Each release carries, next to its packages:

| File | Made by | What it is |
|---|---|---|
| `SHA256SUMS.txt` | CI | `sha256sum` output, one `<64 hex>  <file name>` line per asset, sorted by name |
| `SHA256SUMS.txt.sigs` | trustees, offline | JSON: what is signed, plus one `{k, ed, ml}` entry per trustee |
| `trust-chain.json` | trustees, offline | only once the trustee list has changed: every change, signed by the list it replaces |

## What the app checks

The update check (`UpdateCheck.cs`, Tools › Check for updates) downloads those three files from the newer
release's assets and offers the update only when:

- `SHA256SUMS.txt.sigs` is for exactly this `SHA256SUMS.txt` and this tag;
- at least 2 distinct trustees on the list signed it, each with **both** algorithms valid (one valid and
  one wrong is no vote; a key outside the list is no vote; a key signing twice is one vote);
- the trustee list it names is the one built into this Jay, or reachable from it through
  `trust-chain.json`, each step signed by enough of the list before it;
- its release seq is not lower than the highest this Jay has accepted (kept in `release-seq.txt` in the
  data folder), so an old signed release can't be passed off as new.

Anything else: the app says the version was found but did not pass verification, and shows no button.
When the user downloads a package from a verified release, it is kept only when its SHA-256 matches the
signed list.

About › **Verify a Jay download** runs the same check on a file the user already downloaded (with
`SHA256SUMS.txt`, `.sigs` and `trust-chain.json` next to it). `jay-server release verify` does the same on
the command line.

## What this protects and what it doesn't

- **Protects:** updates and re-downloads for people who already run a genuine Jay. A stolen GitHub
  account, a compromised CI run, a swapped mirror file or **a single trustee** cannot produce a valid
  release; ML-DSA-65 keeps that true against a future quantum computer that breaks Ed25519.
- **Doesn't protect:** two trustees acting together, or the very first download (that copy of Jay has
  no reason to trust anything yet). It also doesn't remove Windows SmartScreen / Smart App Control or
  macOS Gatekeeper warnings; those need real code signing, which Jay doesn't have yet.
- **For now there are two release keys, and both belong to one trustee** (their own key and an offline cold
  backup), with both needed. Until a third trustee joins, this guards against one stolen key, not against that one
  person. Each key has a checked paper backup. If a key and its paper are both lost, old installs can't follow a
  trust-chain step, so they have to reinstall from a fresh download.
- Losing two of the three keys can't be repaired by a trust-chain step; old installs then have to
  reinstall from a fresh download.
- A self-signed Authenticode certificate is not a substitute. Windows doesn't trust it, and asking
  users to install it as a trusted root would weaken their whole system.

## Signed bytes

Release: the lines `jay-release-v2`, tag, seq, commit (40 hex), SHA-256 of `SHA256SUMS.txt` (hex), trustee
list hash, release log head (64 zeros until the log exists), each followed by `\n`.

Trustee list hash: SHA-256 (hex) of `jay-trust-list-v1\n`, the number needed, `\n`, then each full public key
(base64, Ed25519 ‖ ML-DSA-65) followed by `\n`, sorted by key id.

Trustee list change: the lines `jay-trust-change-v1`, old list hash, new list hash, seq, each followed by `\n`.

`SHA256SUMS.txt.sigs`:

```json
{ "v": 2, "tag": "v0.9.0", "seq": 12, "commit": "…40 hex…", "sums": "…64 hex…",
  "trust": "…64 hex…", "log": "…64 hex…",
  "sigs": [ { "k": "<key id>", "ed": "<Ed25519 sig>", "ml": "<ML-DSA-65 sig>" } ] }
```

Unknown fields make the file invalid.

## Release steps

The CI release job publishes the packages and `SHA256SUMS.txt` as a **draft** release. Then:

```bash
# Anyone (no key needed): make the unsigned signature file. --seq is one more than the last release.
gh release download v0.9.0 --pattern 'SHA256SUMS.txt' --dir rel
jay-server release prepare --tag v0.9.0 --seq 12 --commit <full commit id> --sums rel/SHA256SUMS.txt

# Each of two trustees, on their own offline machine:
jay-server release cosign --key jay-release.key --sums rel/SHA256SUMS.txt
```

`cosign` prints the version, seq, commit, every file and hash, the trustee list and who already signed
(with whether each signature is valid), then asks the trustee to type the version. Check the commit
against the repository before typing. The file is passed from one trustee to the next on a USB stick;
the private keys never move.

Finally upload `SHA256SUMS.txt.sigs` (and `trust-chain.json`, if there is one) to the draft, check one
package end to end, and publish:

```bash
gh release upload v0.9.0 rel/SHA256SUMS.txt.sigs
gh release download v0.9.0 --pattern 'Jay-win-x64.zip' --dir rel
jay-server release verify rel/Jay-win-x64.zip --tag v0.9.0
gh release edit v0.9.0 --draft=false
```

## EasyTier builds

Jay's hardened EasyTier ([Yumao-Feather/jay-easytier](https://github.com/Yumao-Feather/jay-easytier))
is signed the same way: 2 of the 3 trustees' **release keys**, both algorithms (never the network trust keys). Its signature names the component, so the
signed bytes start with `jay-component-v1` and `easytier` instead of `jay-release-v2`, and the JSON has
`"component": "easytier"`. A Jay release signature can never pass as an EasyTier one, or the other way round.
The tag is the fork's own (`jay-v2.6.4-1`) and the seq counts EasyTier builds (1, 2, …), separately from Jay.

Jay doesn't download this signature. It ships it: `src/Amber.Core/Multiplayer/EasyTierSigned/` holds the
release's `SHA256SUMS.txt` and `SHA256SUMS.txt.sigs`, and `EasyTierRelease` pins the tag, seq, full commit
and every package hash. Before EasyTier is installed, Jay checks that enough trustees signed exactly that
build and that every pinned hash is in the signed list. Until release keys are in `JayTrust.ReleaseKeys`, only the
pinned hashes are checked. Once they are, a build without a valid EasyTier signature won't install
EasyTier, and `EasyTierSignatureTests` fails, so such a build can't pass CI.

What this adds over the pinned hashes alone: changing the pins in a Jay pull request is not enough to ship
a different EasyTier. Someone also needs two trustees to sign that EasyTier build.

When the fork's release job has published the zips, `SHA256SUMS.txt` and `SIZES.txt` as a draft:

```bash
gh release download jay-v2.6.4-1 --repo Yumao-Feather/jay-easytier --pattern 'SHA256SUMS.txt' --dir et
jay-server release prepare --component easytier --tag jay-v2.6.4-1 --seq 1 --commit <full commit id> --sums et/SHA256SUMS.txt

# Each of two trustees, on their own offline machine (the review says it is an EasyTier build):
jay-server release cosign --key jay-release.key --sums et/SHA256SUMS.txt

gh release upload jay-v2.6.4-1 --repo Yumao-Feather/jay-easytier et/SHA256SUMS.txt.sigs
jay-server release verify et/<one package>.zip --component easytier --tag jay-v2.6.4-1
gh release edit jay-v2.6.4-1 --repo Yumao-Feather/jay-easytier --draft=false
```

Then copy `SHA256SUMS.txt` and `SHA256SUMS.txt.sigs` into `src/Amber.Core/Multiplayer/EasyTierSigned/`, and
update `ReleaseTag`, `Commit`, `FullCommit`, `Seq`, sizes and hashes in `EasyTier.cs`. When the trustee list
changes, the trustees on the new list sign the current EasyTier build again (with the same seq), because
the shipped signature must name the list built into that Jay.

## Changing a trustee

A new list of release keys (one full public key per line, from `jay-server network key-public`) must be signed by enough of the
**old** list:

```bash
jay-server release trust-change --key jay-release.key --to new-trustees.txt --need 2 --seq 13
```

The review shows who stays, who leaves, who joins, and the new list hash; the trustee types its last 6
characters. Once enough old trustees have signed, put the new keys in `JayTrust.ReleaseKeys`, and ship
`trust-chain.json` with every later release so old installs can follow. A change's seq must not be
lower than the last release's seq.
