# Code map

A short, folder-level guide to where things live. It is meant for people and coding agents who need to find the right file before reading code. For a symbol-level index (every type and public member with line numbers), run:

```bash
python3 tools/build-code-index.py            # writes .code-index/ (gitignored): index.md, symbols.tsv, files.tsv
python3 tools/build-code-index.py --check    # fails if a source folder is missing from this map
```

Namespaces are still `Amber.*`; the product name is Jay. Many `///` summaries are in Chinese.

## Projects

| Project | What it is | Entry point |
| --- | --- | --- |
| `src/Amber.Core/` | Everything that isn't UI: launch, install, content, accounts, multiplayer, themes. No Avalonia references. | library |
| `src/Amber.App/` | Avalonia 12 desktop app (MVVM, CommunityToolkit.Mvvm). | `Program.cs` → `App.axaml.cs` |
| `src/Amber.NetworkServer/` | Network server framework (assembly `Jay.NetworkServer`) for public network nodes. | `JayNetworkServer.cs` (`JayNetworkServerBuilder`) |
| `src/Amber.Server/` | `jay-server` CLI: Jay-only MC server with gate and relays, and the public network node. Offline signing review for trust keys. | `Program.cs`, `TrustCommands.cs` |
| `src/Amber.Cli/` | Developer CLI over Core for measurements and scripted tests. | `Program.cs` |
| `src/Amber.Core.Tests/` | xUnit tests, one file per Core area (flat folder). | `dotnet test Amber.Core.Tests` |
| `src/Amber.Mobile/` | Shared Avalonia view for the mobile shells (early). | `App.axaml.cs` |
| `src/Amber.Android/`, `src/Amber.iOS/` | Platform heads for the mobile app (see `docs/android.md`, `docs/ios.md`). | `MainActivity.cs`, `Main.cs` |
| `samples/MyJayNetwork/` | Minimal third-party public network node built on `Jay.NetworkServer`. | `Program.cs` |

Solutions: `src/Amber.slnx` (desktop, server, CLI, tests), `src/Amber.Android.slnx`, `src/Amber.iOS.slnx`. Shared build settings are in `src/Directory.Build.props` and `src/Directory.Build.targets`.

## src/Amber.Core/

Root files: `AmberJson.cs` (source-generated JSON context, AOT-safe), `ArchiveMetadata.cs` (bounded reads of zip metadata), `PlatformFeatures.cs` (what each OS supports).

| Folder | What it covers | Start with |
| --- | --- | --- |
| `src/Amber.Core/Activity/` | Activity center: background tasks and user-facing requests (knocks, file offers). | `TaskCenter.cs`, `RequestCenter.cs` |
| `src/Amber.Core/Auth/` | Microsoft (device code), offline and external (authlib-injector) accounts; secret storage. | `AccountService.cs`, `MicrosoftAuth.cs`, `Accounts.cs` |
| `src/Amber.Core/Bake/` | Experimental "bake" step that calls the universal loader prototype; the pinned loader download. | `BakeService.cs`, `BakeLoaderRelease.cs` |
| `src/Amber.Core/Calls/` | Reserved voice, video and screen sharing (off unless `AMBER_CALLS=1`): call state, who may call, the control confirmation rules. No media yet. | `Calls.cs` |
| `src/Amber.Core/Content/` | Mods, resource packs, shaders, modpacks (Modrinth, CurseForge, MultiMC/Prism and other archives), install queue, recycle bin, crash bisect. | `ModManager.cs`, `ContentInstaller.cs`, `ModpackService.cs`, `ModrinthProvider.cs` |
| `src/Amber.Core/Diagnostics/` | Crash log analysis and the one-click fixes on the "last session" panel. | `CrashAnalyzer.cs`, `CrashFixes.cs` |
| `src/Amber.Core/Game/` | `.minecraft` layout, version JSON model, rules, libraries, NBT (`servers.dat`, `level.dat`), datapacks, world backups. | `GameDirectory.cs`, `VersionModels.cs` |
| `src/Amber.Core/GameLibrary/` | Library home data: instance list, new-instance rules, copy instance, start button state. | `LibraryQuery.cs`, `StartButton.cs` |
| `src/Amber.Core/Help/` | Help center articles (Markdown in `docs/help/`, embedded) and search. | `HelpLibrary.cs` |
| `src/Amber.Core/Tutorial/` | Contextual tips (§9.3): the tip list and the don't-interrupt rules; the app shows them via `Services/TutorialService.cs` and `MainWindow.Tips.cs`. | `Tips.cs` |
| `src/Amber.Core/Identity/` | Jay accounts: BIP39 recovery phrases (embedded wordlists), SLIP-0010 Ed25519 with Jay's own root key, `jay1` Bech32m addresses, device certificates, encrypted account store. | `JayAccount.cs`, `JayAccountStore.cs`, `Bip39.cs` |
| `src/Amber.Core/Install/` | Vanilla, Fabric/Quilt, Forge/NeoForge (new and legacy), OptiFine installers. | `VanillaInstaller.cs`, `ForgeInstaller.cs` |
| `src/Amber.Core/Interop/` | P/Invoke library names for macOS frameworks. | `MacFrameworks.cs` |
| `src/Amber.Core/Java/` | Find local Java, or download the Mojang runtime / Temurin. | `JavaManager.cs`, `JavaFinder.cs` |
| `src/Amber.Core/Launch/` | Build the command line and run the game; per-instance settings, logs, no-trace mode, error reports, graceful exit per OS, the update check and release signature checks. | `GameLauncher.cs`, `LaunchSession.cs`, `InstanceSettings.cs`; release signatures (2 of 3 trustees, trust chain) in `ReleaseSignature.cs`, the signed update check in `UpdateCheck.cs` |
| `src/Amber.Core/Layout/` | UI-independent layout rules: width tiers, density, motion, window sizing. | `LayoutTiers.cs` |
| `src/Amber.Core/Localization/` | UI strings (`en.json` is the source, then one JSON per language), error text, number formatting. | `L.cs`, `en.json` |
| `src/Amber.Core/Multiplayer/` | EasyTier-based rooms: invites, knock, lobby, members, chat, private messages and files, direct link and hole punching, exit (VPN-like) mode helpers, the four choices of opening a room, the room scene (connection mark, "who can see what", the invite panel's choices). | `Invite.cs`, `Knock.cs`, `Lobby.cs`, `EasyTier.cs`, `DirectLink.cs`, `RoomOpening.cs`, `RoomLinks.cs`, `WhoSeesWhat.cs`, `InviteOffer.cs` |
| `src/Amber.Core/Multiplayer/Contacts/` | Contacts and sealed mail: contact codes (JAYC1), the four-emoji check, mail through the room directories, mailed invites and receipts. | `ContactCode.cs`, `ContactBook.cs`, `SealedMail.cs`, `ContactPost.cs` |
| `src/Amber.Core/Multiplayer/EasyTierSigned/` | Data, not code: the hardened EasyTier release's `SHA256SUMS.txt` and the trustees' `SHA256SUMS.txt.sigs`, embedded and checked before EasyTier is installed. | `EasyTier.cs` (`EasyTierRelease.Signature`) |
| `src/Amber.Core/Multiplayer/Files/` | Files in conversations on top of the private channel: card states and actions, kinds, packing worlds / folders / mod lists / masked logs, the queue for friends who are away, the send speed setting. | `FileCards.cs`, `FilePacker.cs`, `FileSendQueue.cs` |
| `src/Amber.Core/Multiplayer/Gate/` | Jay server: gate daemon, invites, signed address records, relays, MC login sniffing, the MC server host. | `GateServer.cs`, `JayServerRunner.cs`, `JayServers.cs` |
| `src/Amber.Core/Multiplayer/Public/` | Public networks: connection strings, room directory protocol, announcer, accounts and login factors, device fingerprints, trust keys (2-of-3 trustees, Ed25519 + ML-DSA-65, sealed key files, paper backup). | `PublicNetwork.cs`, `RoomDirectory.cs`, `NetworkString.cs`, `TrustQuorum.cs`, `TrustKeyFile.cs` |
| `src/Amber.Core/Multiplayer/Transport/` | Crypto and tunnels: Noise, ML-KEM-768, secure record stream, device attestation, security policy, SOCKS5 and game-only exit rules. | `Noise.cs`, `SecureStream.cs`, `SecurityPolicy.cs`, `Tunnel.cs` |
| `src/Amber.Core/Navigation/` | Routes, router history, legacy page routes, command palette index, settings catalog (`settings#field`). | `Route.cs`, `Router.cs`, `SettingsCatalog.cs` |
| `src/Amber.Core/Net/` | Downloader with mirrors and checksums, network-use categories, safe web links. | `Downloader.cs`, `DownloadSource.cs`, `NetworkPolicy.cs` |
| `src/Amber.Core/RemoteControl/` | Remote view/control protocol and sessions. | `RemoteHub.cs`, `RemoteProtocol.cs` |
| `src/Amber.Core/RemoteControl/Unix/` | macOS (CoreGraphics), X11 (XTest) and Wayland (portal) capture and input, stop hotkey. | `X11Remote.cs`, `WaylandRemote.cs`, `MacRemote.cs` |
| `src/Amber.Core/RemoteControl/Windows/` | Windows GDI capture, SendInput, global hotkey. | `WindowsRemote.cs` |
| `src/Amber.Core/Safety/` | "For a child" mode settings. | `ChildMode.cs` |
| `src/Amber.Core/Themes/` | Theme packs (`.jaytheme`, data only): tokens, styles, layout, store, reserved safety words, network themes. Also Jay's own artwork as data: the wing bars, pixel covers and pixel faces. Pseudo-3D motion numbers, the layered scene, and the turning skin figure (`SkinTurntable`, `PixelSkin`). | `ThemePack.cs`, `ThemeStore.cs`, `PixelArt.cs`, `WingBars.cs`, `Motion3D.cs`, `SkinFigure.cs` |

### Codes users paste

`src/Amber.Core/Multiplayer/PastedCode.cs` classifies any pasted code. The formats live in:

| Prefix | Meaning | Parser |
| --- | --- | --- |
| `JAY2-` | Room invite | `Multiplayer/Invite.cs` |
| `JAYDS1-`, `JAYDR1-` | Sealed direct offer / reply (no server; addresses encrypted with a passphrase told another way) | `Multiplayer/SealedDirect.cs` |
| `JAY2D-`, `JAY2R-` | Removed direct-link codes (carried public IPs); refused as `OldDirect` | `Multiplayer/DirectLink.cs` (`OldDirectCodes`) |
| `AMB-` | Legacy room code | `Multiplayer/RoomCode.cs` |
| `JAYS2-` | Jay server invite (domain only, no player name; version 3 carries a signed-record locator instead of an address) | `Multiplayer/Gate/GateInvite.cs`, `Multiplayer/Gate/GateRecord.cs` |
| `JAYSS1-` | Jay server invite sealed with a passphrase (server with no domain) | `Multiplayer/Gate/GateInvite.cs` |
| `JAYC1-` | Contact code (identity public key) | `Multiplayer/Contacts/ContactCode.cs` |
| `JAYR1-` | Relay node code | `Multiplayer/Gate/GateRelayCode.cs` |
| `AMBNODE-`, `JAYNS1-`, `JAYNR1-` | Relay share: domains only / sealed with a passphrase / signed-record locator (relays that go by an IP) | `Multiplayer/RelayNodes.cs`, `Multiplayer/Public/RelayRecord.cs` |
| `JAYNET2-` | Public network connection string | `Multiplayer/Public/NetworkString.cs` |
| `JAYF1-` | Device fingerprint | `Multiplayer/Public/DeviceFingerprint.cs` |

## src/Amber.App/

Root files: `Program.cs` (startup checks, dev flags, Avalonia bootstrap), `App.axaml(.cs)` (app resources and startup), `DevCrashGuard.cs`, `LinuxFonts.cs`, `LinuxStartup.cs`.

| Folder | What it covers | Start with |
| --- | --- | --- |
| `src/Amber.App/Services/` | App-level services: `LauncherService.cs` wires Core together; settings, themes, density, toasts, sensitive clipboard, restart, skin rendering. | `LauncherService.cs`, `AppSettings.cs`, `ThemeService.cs` |
| `src/Amber.App/ViewModels/` | One view model per page, split into partial files by tab (`InstanceViewModel.*.cs`, `MultiplayerViewModel.*.cs`, …). `ShellViewModel.cs` owns navigation. | `ShellViewModel.cs` |
| `src/Amber.App/Views/` | XAML pages and windows. `MainWindow.*.cs` is the shell; `SelfTest*.cs` is the GUI self-check, one file per `AMBER_SELFTEST_SCOPE`. | `MainWindow.axaml`, `SelfTest.cs` |
| `src/Amber.App/Views/Controls/` | Small reusable controls (status bar, the one progress line, confirm button, middle ellipsis). | |
| `src/Amber.App/Controls/Safety/` | Locked safety UI that theme packs cannot restyle (dialogs, panels, privacy row). | `SafetySurface.cs`, `SafetyDialog.cs` |
| `src/Amber.App/Themes/` | Default tokens, control styles, icons, and the locked safety styles. | `Tokens.axaml`, `Safety.axaml` |
| `src/Amber.App/Localization/` | `{l:T key}` markup extension that follows the current language. | `T.cs` |
| `src/Amber.App/Assets/` | Fonts, icons, app icons. | |

## src/Amber.NetworkServer/

`JayNetworkServer.cs` (builder and host: EasyTier seed, room directory, login, room gates), `Gates.cs` (policy and region gates), `AdminApi.cs` (local admin API and token), `NetworkScaffold.cs` (signing key, network JSON, config files for a new network). `src/Amber.NetworkServer/Hardware/` binds a node to its machine (hardware ids, offline certificate, TPM sealing, runtime checks, self-wipe; `docs/security/node-hardware-binding.md`). See `docs/network-server-framework.md`.

## Mobile

`src/Amber.Mobile/` holds the shared mobile app (`src/Amber.Mobile/ViewModels/`, `src/Amber.Mobile/Views/`); `src/Amber.Android/` and `src/Amber.iOS/` are the platform heads.

## Outside src/

| Path | What it is |
| --- | --- |
| `docs/` | Design and operator docs: `public-network.md`, `network-server.md`, `network-server-framework.md`, `themes.md`, `platform-porting-guide.md`, `privacy.md`, `i18n-glossary.md`. |
| `docs/help/` | In-app help articles per language (`zh-Hans`, `zh-Hant`, `en`, `ja`), embedded into Core. |
| `themes/examples/` | Example theme packs (`pixel-night`, `sakura`, `topbar`). |
| `networks/` | Registry of third-party public networks; CI (`networks.yml`) signs them into `JAYNET2-` strings after merge. |
| `node/` | Install scripts and compose file for a community relay node. |
| `server/` | `jay-server` deployment: systemd units, Docker, public node installer. |
| `scripts/` | Release helpers: bundle EasyTier, publish and package macOS, attach the universal loader zip to its release (`attach-loader.ps1`, see `docs/universal-loader-release.md`). |
| `build/` | `pins.json`: every binary Jay bundles or downloads with a fixed hash (source, SHA-256, size), embedded in Amber.Core; see `docs/pins.md`. |
| `tools/` | GUI self-check runner, multiplayer test, NAT bench, brand icon export, this code index builder. |
| `tools/FullLanCheck/` | Headless end-to-end check of full-LAN mode on macOS/Linux (needs root), used by the fulllan-e2e workflow. |
| `tools/jay-dns/` | Java 18+ name resolver for the Windows strong sandbox; built by Amber.Core.csproj and embedded (`WindowsSandbox.ResolverJar`). |
| `tools/jay-fs/` | Java file system provider for the Windows sandbox (fixes `Files.isWritable` under low integrity / AppContainer); `stub/` is compile-only. Embedded (`WindowsSandbox.FileSystemJar`). |
| `.github/` | CI (`build.yml`), security regression tests, network registry checks, issue templates. |

## Where to start for common tasks

| Task | Look at |
| --- | --- |
| A game won't launch or launches with wrong args | `Launch/GameLauncher.cs`, `Game/VersionModels.cs`, `Game/LibraryResolver.cs`, `Java/JavaManager.cs` |
| Loader install fails | `Install/*Installer.cs`, `Net/Downloader.cs` |
| Mod search, download or dependency issue | `Content/ModrinthProvider.cs`, `Content/CurseForgeProvider.cs`, `Content/ContentInstaller.cs`, `Content/InstallQueue.cs` |
| Room join or knock fails | `Multiplayer/Invite.cs`, `Multiplayer/Knock.cs`, `Multiplayer/Lobby.cs`, `Multiplayer/EasyTier.cs` |
| Encryption or handshake | `Multiplayer/Transport/Noise.cs`, `SecureStream.cs`, `MemberChannel.cs`, `DeviceAttestation.cs` |
| Public network rooms list | `Multiplayer/Public/RoomDirectory.cs`, `RoomAnnouncer.cs`, `src/Amber.NetworkServer/JayNetworkServer.cs` |
| A new setting | `Navigation/SettingsCatalog.cs`, `src/Amber.App/Services/AppSettings.cs`, `src/Amber.App/ViewModels/SettingsViewModel.*.cs`, `Localization/en.json` |
| A new UI string | `Localization/en.json` first, then every other language file |
| A new page or route | `Navigation/Route.cs`, `src/Amber.App/ViewModels/ShellViewModel.cs`, `src/Amber.App/Views/MainWindow.Shell.cs` |
| Theme pack behaviour | `Themes/ThemePack.cs`, `Themes/ThemeStyles.cs`, `src/Amber.App/Services/ThemeService.cs` |

Paths in this table without a project prefix are under `src/Amber.Core/`.
