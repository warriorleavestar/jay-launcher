# Release signatures

Jay releases carry a `SHA256SUMS.txt` built by CI. The maintainer signs it offline with Jay's
**release key** and uploads `SHA256SUMS.txt.sig` to the same GitHub Release.

A Jay that already has the release public key built in (`ReleaseSignature.TrustedKeys` in
`src/Amber.Core/Launch/ReleaseSignature.cs`) can then check any downloaded package. Three things must
hold:

- The signature comes from a trusted release key.
- It covers this exact `SHA256SUMS.txt` and the version the user asked for. Binding the tag stops an
  old signed release being passed off as a new one.
- The package's file name and SHA-256 are listed in it.

## What this protects and what it doesn't

- **Protects:** updates and re-downloads for people who already run a genuine Jay. A stolen GitHub
  account, a compromised CI run or a download mirror that swaps files cannot produce a valid
  signature, because the private key never leaves the maintainer's offline machine.
- **Doesn't protect:** the very first download, because that copy of Jay has no reason to trust
  anything yet. It also doesn't remove Windows SmartScreen / Smart App Control or macOS Gatekeeper
  warnings. Those need real code signing, which Jay doesn't have yet.
- A self-signed Authenticode certificate is not a substitute. Windows doesn't trust it, and asking
  users to install it as a trusted root would weaken their whole system.

## Keys

- The release key is an Ed25519 key, separate from the directory root key and from network signing
  keys. Never reuse one key for two jobs.
- Keep the private key offline: a USB stick, plus a paper backup of the base64 seed. Never put it in
  the repo, CI secrets, a server or a cloud drive.
- To rotate, add the new public key to `TrustedKeys` and ship a release signed with the **old** key.
  Remove the old key later.

## Maintainer steps

One time:

```bash
jay-server release key --out jay-release.key
# Prints the public key; add it to ReleaseSignature.TrustedKeys in a PR.
```

For every release, after the CI release job has published the assets:

```bash
gh release download v0.9.0 --pattern 'SHA256SUMS.txt' --dir rel   # or download it from the Release page
jay-server release sign --key jay-release.key --tag v0.9.0 --sums rel/SHA256SUMS.txt
gh release upload v0.9.0 rel/SHA256SUMS.txt.sig
```

Before uploading, it's worth checking one package end to end:

```bash
gh release download v0.9.0 --pattern 'Jay-win-x64.zip' --dir rel
jay-server release verify rel/Jay-win-x64.zip --tag v0.9.0
```

## File formats

- `SHA256SUMS.txt`: `sha256sum` output, one `<64 hex>  <file name>` line per asset, sorted by name.
  Lines with paths, and names listed twice with different hashes, are ignored.
- `SHA256SUMS.txt.sig`:

  ```
  jay-release-signature-v1
  tag: v0.9.0
  key: <base64 Ed25519 public key>
  sig: <base64 signature>
  ```

  The signed message is `"jay-release-v1\n" + tag + "\n" + hex(SHA-256(SHA256SUMS.txt)) + "\n"`.
