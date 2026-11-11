# Pinned binaries (`build/pins.json`)

`build/pins.json` lists every binary that Jay bundles or downloads with a fixed hash. For each one it gives the source and the SHA-256 (or a content address), so you can check exactly what a Jay build will fetch. This is the pins part of privacy plan item 4.

## What is in it

Each entry has these fields:

| Field | Meaning |
|---|---|
| `id` | Stable name (`jay-loader`, `easytier-<platform>`, …) |
| `name`, `version`, `tag`, `commit`, `platform` | What it is |
| `file`, `urls` | The exact file name and where it is downloaded from (HTTPS only; GitHub download mirrors are tried with the same URL behind a prefix, and the hash decides) |
| `source`, `license` | Source repository and licence |
| `sha256` (or `sha1` for content-addressed files), `size` | The hash Jay checks before it uses the file |
| `usedBy` | Every place in this repository that downloads or names the file |

`notPinned` lists downloads that are not fixed in advance, and what checks them instead. Examples are game files checked by Mojang's manifests, and mods checked by the hashes Modrinth and CurseForge return.

## How it is used and checked

- `src/Amber.Core/Amber.Core.csproj` embeds the file. The universal loader download (`BakeLoaderRelease.Pinned`) reads its pin straight from it.
- `scripts/attach-loader.ps1` reads the `jay-loader` pin from it.
- Some pins are also written elsewhere: EasyTier in `EasyTier.cs`, `scripts/bundle-easytier.sh` and the relay node install scripts, and the Java runtime manifest in `JavaRuntimeInstaller.cs`. `PinsTests` (run by CI's validate job) fails when any of them disagrees with `pins.json`. It also fails when a pin lists a file in `usedBy` that doesn't exist, or a `usedBy` file doesn't mention the pin.
- `scripts/bundle-easytier.sh` checks the size and SHA-256 of the EasyTier package it bundles before putting it in a release.

To change a pin, edit `pins.json` first, then the places the test names.

## Not done yet (rest of item 4)

Deterministic builds, lock files and a `repro.sh` that rebuilds a release and compares hashes are not part of this file yet.
