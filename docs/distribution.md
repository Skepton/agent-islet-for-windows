# Distribution plan

Goal: someone non-technical downloads one file from GitHub, double-clicks it, and it works. No installer. Updates come from GitHub Releases.

## The release
- **One portable exe, nothing else**: self-contained single file (`win-x64`), about 72 MB compressed (measured 2026-10-16; WPF cannot be trimmed). No helper program and no copying: Claude Code's `http` hook type calls the app's local endpoint directly (docs/findings.md H1-H8), so the app can run from anywhere, including Downloads.
- **Connect** (the panel's Claude Code tab, built) edits `~/.claude/settings.json`: it asks first, backs the file up, adds only our entries, and **Disconnect** removes them (SignPath's "warn before changing system configuration" and "uninstall" rules). The panel opens on that tab until Claude Code is connected.
- The old helper `island-hook.exe` and the named pipe still work (the app listens on both the current and the pre-rename pipe name), for existing installs and `scripts\install-local.ps1`. Remove them from the release path; keep the project for development until nothing needs it.
- **Never commit binaries.** A GitHub Actions workflow builds on a version tag, signs, and attaches the exe plus a SHA-256 checksum to a GitHub Release.
- The framework-dependent build `install-local.ps1` makes (441 KB, needs the .NET 8 desktop runtime) stays for development only.
- **Known limits of the web hooks:** SessionStart is not supported by Claude Code's `http` hooks (not needed); `CLAUDE_PROJECT_DIR` is not available, so a subagent's project is derived from its worktree path (`ProjectFolder.OfWorktree`, to be checked against a real subagent payload); a managed policy that restricts hook URLs would block them; the port may be taken (the app then picks a free one and Connect has to be repeated).

## Updates
Opt-in check against `GET /repos/<owner>/<repo>/releases/latest` (no user data sent). When newer: "Update available" in the panel and tray; download the exe, verify the SHA-256 from the release, rename the running exe aside, put the new one in place, restart. Alternative: Velopack (less code, but it installs into AppData).

## Signing with SignPath Foundation (free for open source)
Decided 2026-10-16: try SignPath. Licence is GPL-3.0 (OSI-approved, as they require). Terms read at https://signpath.org/terms:
- All components OSI-licensed, no proprietary code, no dual licensing. The signing team must own and maintain the repository.
- Signed binaries are built only from the repo, **in CI**; build scripts count as source. Every release needs a **manual approval** in SignPath.
- Enforced metadata: **product name = project name**, same product version across builds.
- Project must already be **released in the form to be signed** (publish one unsigned release first), actively maintained, with a download page. Initial checks cover the project's reputation; a brand-new repo with no users may be declined or asked to wait.
- Roles with MFA on SignPath and GitHub: author, reviewer, approver.
- Home page and download page need a **"Code signing policy"** section: the line "Free code signing provided by SignPath.io, certificate by SignPath Foundation", the team roles and members, and the privacy statement. Add this only once accepted.
- Software that sends data must disclose it in a privacy policy and be switchable off (our usage tracking is opt-in and off by default; the update check sends nothing about the user). It must warn before changing system configuration and be uninstallable.
- The certificate is issued to **SignPath Foundation** (they are the publisher shown), and can be revoked if rules are broken.
- Applying is the owner's act (the "Apply" page on signpath.org), under their own accounts.

### To do before applying
1. ~~Decide the name and rename the repo~~ Done 2026-10-16: **Agent Islet**, repo `Skepton/agent-islet-for-windows`. The signing team must own the repo, so check that the account that owns it is the one that applies.
2. Make the repo public; add `LICENSE` (done) and a README download page.
3. Set the product name and version in the App and Hook csproj files (`<Product>`, `<Version>`), matching the project name.
4. Release workflow (build, checksum, GitHub Release), then the first unsigned release.
5. ~~First-run setup and "Remove hooks"~~ Done (Claude Code tab). Still to do: the update check.
6. Apply; once accepted, add the signing step (`signpath/github-action-submit-signing-request`) and the code signing policy section.

### Unverified
- Whether Smart App Control on this machine accepts a SignPath-signed exe. It checks for a valid signature from a trusted CA, so it should; test with the first signed release.
- SmartScreen still builds reputation per publisher and file, so the first downloads may show a warning even when signed.
