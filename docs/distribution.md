# Distribution plan

Goal: someone non-technical downloads one file from GitHub, double-clicks it, and it works. No installer. Updates come from GitHub Releases.

## The release
- **One portable exe**, self-contained single file (`win-x64`, compressed): about 72 MB measured on 2026-10-16 (WPF cannot be trimmed). The 17 MB forwarder `island-hook.exe` is embedded in it and copied to `%LOCALAPPDATA%` on first run, so the hooks survive moving the exe.
  - The forwarder stays a separate exe: it runs on every Claude Code event and has to start fast (45 ms median), which a WPF exe cannot.
- **First run** offers a "Set up Claude Code" button. It edits `~/.claude/settings.json` through the existing installer (backup, only our entries). The app must say what it will change before it does (SignPath rule), and has an "Remove hooks" action (uninstall rule).
- **Never commit binaries.** A GitHub Actions workflow builds on a version tag, signs, and attaches the exe plus a SHA-256 checksum to a GitHub Release.
- The framework-dependent build `install-local.ps1` makes (441 KB, needs the .NET 8 desktop runtime) stays for development only.

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
1. **Decide the name**, then rename the GitHub repo. It is still `Skepton/win-ai-squared` (the discarded name), owned by `Skepton`; the signing team must own the repo.
2. Make the repo public; add `LICENSE` (done) and a README download page.
3. Set the product name and version in the App and Hook csproj files (`<Product>`, `<Version>`), matching the project name.
4. Release workflow (build, checksum, GitHub Release), then the first unsigned release.
5. First-run setup button, "Remove hooks" action, update check.
6. Apply; once accepted, add the signing step (`signpath/github-action-submit-signing-request`) and the code signing policy section.

### Unverified
- Whether Smart App Control on this machine accepts a SignPath-signed exe. It checks for a valid signature from a trusted CA, so it should; test with the first signed release.
- SmartScreen still builds reputation per publisher and file, so the first downloads may show a warning even when signed.
