# Universal loader package: how Jay gets it

Jay's experimental bake needs the universal loader. The loader is a separate project: Jay does not build it and it is not bundled with Jay. Jay downloads one pinned zip from a GitHub release of the loader's own repository, [warriorleavestar/jay-loader](https://github.com/warriorleavestar/jay-loader/releases). The release root is one value in the pin (`ReleasesBase`), so moving the asset elsewhere is a one-line change.

If the pin is ever left empty (no tag or hash), **Download loader** stays disabled and says the loader has not been published, and the launch status box only explains why baking was skipped.

## What Jay does

- **Settings › Experimental › Download loader**, or the **Download loader** button in the launch status box when baking is on and no loader is installed:
  1. Downloads `<ReleasesBase>/<tag>/<asset>`; `ReleasesBase` is `https://github.com/warriorleavestar/jay-loader/releases/download/` today. It tries GitHub and the same GitHub download mirrors as the multiplayer component; mirrors go first when Jay prefers mirrors. The download can be resumed and cancelled.
  2. Accepts the file only if its size and SHA-256 equal the `jay-loader` pin in `build/pins.json`. A different file is thrown away, and the next source starts clean.
  3. Installs it exactly like **Install loader from zip…**:
     - Every file is checked against the package's `manifest.json`, and a file the manifest does not list is refused.
     - The package is unpacked to `<Jay data>-runtime/universal-loader/<loaderVersion>` (the runtime folder beside the data folder, which holds no settings, accounts or keys; older installs are moved there once), then selected.
- Before every bake, warm or cleanup, every file of the selected package is checked again against its manifest.

## Releasing a new loader

1. **Build the package** in the loader project, on the maintainer's build machine:
   ```bash
   bash build.sh
   python -I tools/package.py
   ```
   This writes `build/dist/amber-ul-<loaderVersion>.zip`.
2. **Publish it** as a release of the loader repository. The loader project does this; the tag is `v<loaderVersion>`, and the asset is the full package `amber-ul-<loaderVersion>.zip`, not `-core.zip`.
3. **Update the pin**: the `jay-loader` entry in `build/pins.json` (see [pins.md](pins.md)):
   - `version` (loader version), `tag`, `file` (asset name)
   - `urls`: `https://github.com/<repo>/releases/download/<tag>/<file>` (the repository is read from it)
   - `sha256` and `size` of that exact zip

   Ship that change in a Jay build. A Jay version only ever downloads the zip it is pinned to.
4. **Check the zip** against the pin. This uploads nothing:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\attach-loader.ps1 -Zip <path>\amber-ul-<loaderVersion>.zip -Check
   ```
5. **Attach it** yourself only if the loader project did not, and only with the owner's go-ahead. Run the same command without `-Check`; the repository comes from the pin's release root. It asks before it does anything:
   - If the release for the tag doesn't exist yet, it creates it as a pre-release with the zip attached.
   - If the release exists, it uploads the zip to it; pass `-Replace` to overwrite an asset of the same name.

   The manual equivalent:
   ```bash
   gh release create v<loaderVersion> amber-ul-<loaderVersion>.zip --repo warriorleavestar/jay-loader --prerelease --title "Universal loader <loaderVersion>"
   ```
   Never replace an asset that a shipped Jay is pinned to with a different file: those Jays will refuse it.

## Licences (required before publishing)

The zip is redistributed to players, so it must carry the licence notices of everything inside it:
- a top-level licence / third-party notice file (or folder)
- each notice file listed in `manifest.json` `files`, because Jay refuses files the manifest does not list

The released 0.3.0 package does: `LICENSE`, `THIRD_PARTY_NOTICES.md` and `licenses/*`, all listed in its manifest, so Jay checks them on install like every other file.

Third-party pieces in the 0.3.0 package:

| Piece | Licence |
| --- | --- |
| ASM 9.10.1 (asm, analysis, commons, tree, util) | BSD-3-Clause |
| Sponge Mixin 0.17.5 (Fabric's fork) | MIT |
| MixinExtras 0.5.5 | MIT |
| Gson 2.10.1 | Apache-2.0 |
| ANTLR 4 runtime 4.11.1 | BSD-3-Clause |
| tiny-remapper 0.10.4, mapping-io 0.6.1, access-widener 2.1.0 (FabricMC) | Apache-2.0 |
| Fabric intermediary mappings 1.21.1 | CC0-1.0 |
| tomlj 1.1.1 | Apache-2.0 |
| XZ for Java 1.9 | public domain / 0BSD |
| Fabric API classes in the bridge jars, if they are copied or derived (`amber-impl/fabric-*`) | Apache-2.0 |

The package must not contain:
- Mojang files or Mojang mappings
- patched or repackaged Forge / NeoForge / Minecraft classes

Platform installers and official mapping tables are downloaded by Jay on the player's machine and stay there.
