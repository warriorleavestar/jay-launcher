# Notes for coding agents and reviewers

The coding rules for this repository are in [CLAUDE.md](CLAUDE.md) and [CONTRIBUTING.md](CONTRIBUTING.md). They apply to every agent, not only Claude. Read them before changing code.

## Review guidelines

### What to check

- Correctness, concurrency (races, deadlocks, `async` work that outlives its owner), resource leaks (sockets, processes, files, timers), cancellation and timeouts.
- Compatibility: wire formats, codes and file formats are versioned. A change that an older Jay can't read needs a new version or a compatibility path.
- Tests: new behaviour has unit tests, and a GUI self-check step when it is visible in the UI.

### Repository rules (a broken rule is blocking)

- No telemetry, analytics or remote kill switches. Network access only for user-initiated downloads, Microsoft login, the Modrinth and CurseForge APIs, the update check and multiplayer. A new network destination must fit one of these.
- A new feature that connects to the network or makes something public is off for users who upgrade from an older version; only new installs may have it on.
- No public IP addresses, personal data or machine-specific paths in code strings, tests, comments or docs (see the privacy section below).
- Tests and self-checks use a separate test game directory and `AMBER_DATA_DIR`. Nothing may write to a real `.minecraft`.
- Established cryptographic primitives and libraries only.
- `Amber.Core` stays free of UI dependencies. Platform-specific code is behind `OperatingSystem.IsWindows()` (or the matching check) or in a platform-only type.
- Original code only: nothing copied or ported from other launchers or decompiled binaries.
- Secrets never go into logs, diagnostics or exports. Exported logs and crash reports go through the existing redaction.

### UI text (i18n)

- No user-visible text written directly in `Amber.App` or in `.axaml`. Use `{l:T key}` in XAML and `L.T` / `L.F` / `L.P` in C#.
- A new key is added to `en.json` first and then to every other language file (`lzh.json` may lag). Placeholders like `{0}` are kept in every language. Keys stay sorted.
- Terms follow `docs/i18n-glossary.md` and the official Minecraft terms for each language.

### `.axaml` and layout changes

- Say in the review that the change needs a maintainer to look at screenshots before merging; don't mark the layout itself as correct.
- Check that it still works at narrow widths (600×420) and with longer translations (text wraps or trims, nothing is cut off).
- Safety surfaces (confirmation dialogs, knock and file prompts, the privacy line) keep the default focus on the safe choice and keep their fixed look; themes must not be able to restyle them.
- Sensitive codes and tokens stay masked on screen.

### How to write the review

- Write review comments in Simplified Chinese (the maintainers' working language). Keep code identifiers, file names and commands as they are.
- Each finding: file and line, what goes wrong, how it is triggered, and a suggested fix. Point out missing tests for new behaviour.
- Severity:
  - **Blocking (P0/P1)**: wrong behaviour users will hit, data loss, a crash, a broken build or test, a repository rule below broken, or a change that breaks compatibility without a version bump.
  - **Medium (P2)**: an edge case that fails, a resource or thread leak, a missing timeout or limit, missing tests for new behaviour, a confusing or wrong user-facing text.
  - **Low (P3)**: naming, comments, small simplifications, style.
- Don't report pure style preferences as blocking. Don't repeat a finding that an earlier review on the same lines already made unless the new commit didn't fix it.
- Never approve, merge, close or push. Only comment.

### Security findings stay out of public comments

This repository is public. Maintainers run a separate, private security review for every pull request.

- If you see a possible security vulnerability (a way to bypass a check, read or change data you shouldn't, crash or overload a service from outside, escalate privileges, or recover a secret), **don't describe it in the review**. Write one line only: `可能有安全问题，已交给维护者私下处理。` No file, line, attack steps or impact.
- Don't write about weaknesses that aren't fixed yet, limits that can be bypassed, or how a protection could be defeated, even as a side remark or a suggestion.
- Normal hardening advice that doesn't point at an exploitable gap (for example "add a unit test for the malformed input path") is fine.

### Privacy in the pull request itself

Flag as blocking anything in the diff, the commit messages or the pull request text that contains:

- a public IP address (use `example.org`, `192.0.2.x`, `198.51.100.x`, `203.0.113.x` or `2001:db8::` in tests and docs),
- an email address, a real name, a computer user name or host name, or a machine-specific absolute path,
- a key, token, password or other secret.
