# Handoff (end of session 2)

Read this, then `CLAUDE.md` (ground rules) and `PLAN.md` (architecture, work packages, status). Evidence for every Claude Code behaviour we rely on is in `docs/findings.md`; the target look is in `docs/design.md`.

## What this is
A Windows-only, Claude Code-only "island" that drops down from the top-centre of the screen when Claude Code needs the user (permission approval, waiting for input), shows usage milestones for the 5-hour and 7-day limits, and lets the user approve or deny from the island. C# / .NET 8 / WPF. Inspired by Open Island (macOS).

## State right now
- Branch `main`, pushed to `git@github.com:Skepton/win-ai-squared.git` (SSH key in `~/.ssh/id_ed25519`). Git identity `xeino <xeinon@gmail.com>` is correct.
- Every commit builds. 235 tests pass (Core 128, Claude Code 107). `smoke-app` and `smoke-app-ui` pass after the agent split; `smoke-overlay` and `smoke-usage` were not re-run since.
- **Agent split (session 3):** all Claude Code code now lives in `src/Island.ClaudeCode`; Core is agent-neutral behind `IAgentIntegration` (PLAN §4.1). Pipe protocol is v2 (PLAN §4.2).
- **Project approval + installer (session 3):** see PLAN §8. The user runs the island for real via `scripts/install-local.ps1` (hooks in their real `~/.claude/settings.json`, intentionally; see CLAUDE.md). While that island runs, a dev `dotnet run` exits at once (single instance) and the smoke scripts refuse to start: quit it from the tray first. A dev build then receives the same live events.
- `smoke-app-ui` failed once (no button found in 8 s, right after `smoke-app`) and passed on rerun: the background job that runs `simulate` can start slowly. Script timing, not a product bug.
- Since protocol v2 an old `island-hook.exe` and a new app (or the reverse) do not understand each other; that fails open (silence). Republish both together (`install-local.ps1` does).
- **WP4 (the visible island) is done.** Session 2 added: `CardPresenter` (Core), the card view-models, `IslandWindow`, `OverlayController`, `--demo`, `scripts/smoke-overlay.ps1`.
- **Nothing is installed in the user's Claude Code settings.** The installer (WP6) does not exist, so no real Claude Code session reaches the app yet. Next job.

## Build, test, run
```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')  # if dotnet is not found
dotnet build ClaudeCodeIsland.sln
dotnet test ClaudeCodeIsland.sln
powershell -File scripts\publish-hook.ps1          # artifacts\hook\island-hook.exe (needed by the smoke scripts)
powershell -File scripts\smoke-app.ps1             # launches the real app, drives it with the real forwarder
powershell -File scripts\smoke-app-ui.ps1          # clicks the debug window's buttons via UI Automation
powershell -File scripts\smoke-overlay.ps1         # the island: focus safety + REAL mouse clicks (moves the mouse for a second)
powershell -File scripts\smoke-usage.ps1           # ONE live usage poll against a throwaway data folder
dotnet run --project src/Island.App                # debug window + island; flags: --tray (start hidden), --demo (cycle fake cards), --poll-usage (opt in to usage polling)
.\artifacts\hook\island-hook.exe simulate tests\fixtures\hooks\PermissionRequest.with-suggestions.json 120   # island appears; click it; JSON prints
```
If PowerShell refuses to run a script ("running scripts is disabled"), run it as `powershell -NoProfile -ExecutionPolicy Bypass -File ...` (process scope only). All smoke scripts refuse to run if an island instance is already running (they never kill the user's). If the user has the app open, the exe is locked and `dotnet build` of the App fails: compile into a scratch folder with `-o <dir>`, or ask them to quit it from the tray.

## Map
```
src/Island.Core        agent-neutral: IAgentIntegration + AgentEvents + PermissionVerdict, sessions, hub + transport,
                       usage engine (MilestoneEngine, UsageMonitor, PollSchedule), overlay: OverlayState, CardPresenter, UsageFormat. No UI.
src/Island.ClaudeCode  ClaudeCodeAgent (payload → events, verdict → hook output), ClaudeCodeHook (forwarder side),
                       HookPayload, PermissionDecision, SuggestionChoice, ToolSummary, Usage/ (OAuth credentials, source, parser)
src/Island.Hook        island-hook.exe forwarder: `hook` (stdin) and `simulate <file> [secs]`. Always exit 0, silent on error.
src/Island.App         WPF app: App.xaml.cs (single instance, quit signal, tray, hosts server + hub, registers the agents, flags),
                       PanelWindow + PanelViewModel + ProjectsViewModel (dark control panel: Sessions / Projects / Debug),
                       IslandWindow (the island + collapsed pill) + OverlayViewModels + OverlayController + DemoDriver,
                       TrayIconHost (dark menu, left-click = panel), Theme.xaml (shared styles), NativeMethods.cs
tests/                 Island.Core.Tests (TestAgent), Island.ClaudeCode.Tests + tests/fixtures/{hooks,usage} (real, sanitised payloads)
scripts/          publish-hook, bench-hook, smoke-app, smoke-app-ui, smoke-overlay, smoke-usage
```
Key flow: Claude Code hook -> `island-hook.exe` (`ClaudeCodeHook`) -> named pipe `\\.\pipe\claude-code-island-<SID>` -> `IslandServer` -> `IslandHub` -> `ClaudeCodeAgent.Translate` (neutral `AgentEvent`) -> `SessionStore` (state machine) -> effects -> `OverlayController.OnEffects` -> `OverlayState` -> `OverlayViewModel` -> `IslandWindow`. A permission request keeps its pipe connection open until `IslandHub.Answer(verdict)` is called; the broker asks the owning integration to encode the verdict, and the forwarder prints that text verbatim. A null verdict means "no decision", so Claude Code shows its own prompt. Usage: `UsageMonitor` -> `OverlayController.OnUsage` (strip + milestone cards).

Design notes for the overlay code:
- Logic lives in Core (`CardPresenter`) because there is no App test project; the view-models only hold values. Agent-specific strings (headline, `$`/`PS>`, "Always Allow (Bash)", the agent chip) come from the integration via `PendingPermission` / the effects' `Agent`. A card view-model is rebuilt when the card *instance* changes (notices reuse the id `notice:<session>`).
- `--demo` cards have ids starting `demo-`; the controller answers those locally instead of calling the hub.
- The window is placed at the top-centre of the work area of the monitor holding the foreground window whenever the front card changes.

## What is left, in order

### 1. WP4 loose ends (unverified on this machine; none blocks WP6)
- Windows Terminal in front for the focus test (only VS Code was tried). Run `smoke-overlay.ps1` with it focused.
- Click-through strip (`WS_EX_TRANSPARENT`) over a title bar; top taskbar placement; mixed-DPI monitors (only one 150 % monitor here).
- A finer visual pass against `demo.gif` (frames 145/215/291; download from `https://raw.githubusercontent.com/Octane0411/open-vibe-island/main/docs/images/demo.gif` into a scratch folder). Currently close; ideas: tighter vertical spacing, long needs-input titles are ellipsised early by the chips, the reply scroll area cuts a line mid-way, icons for sound/settings on the strip are not built.
- AskUserQuestion options as buttons (design.md "Other states") are not built; such questions fall back to "Answer in terminal" via the notice card.

### 2. WP6 installer (needed before any real use)
`island-hook install|uninstall [--settings <path>] [--dry-run]`. Merge hook entries for SessionStart, UserPromptSubmit, PermissionRequest (timeout 600), PostToolUse, Notification, Stop, SessionEnd (timeout 2). **Not** PreToolUse. Command form: `"C:/abs/path/island-hook.exe" hook` (Claude Code runs hooks under Git Bash on Windows: forward slashes, quoted). Copy exes to `%LOCALAPPDATA%\ClaudeCodeIsland\bin\`. Back up settings first, preserve everything else, write UTF-8 **without BOM** (`[IO.File]::WriteAllText` with `UTF8Encoding($false)`), idempotent, uninstall removes only our entries, test only against temp copies via `--settings`. Test matrix in PLAN WP6. No status line in v1.

### 3. WP7
Settings window, first-run consent for usage polling (today `--poll-usage` stands in), thresholds, start-with-Windows, publish script for the app (single-file, self-contained, WPF cannot be trimmed), README.

### 4. Still open from earlier
- Does answering the dialog in VS Code kill a waiting hook process? Which events fire for Edit/WebFetch/MCP/plan mode/AskUserQuestion? Test with the real app once WP6 exists (`docs/findings.md`, "Open tests").
- Forwarder gets `ClientApp` from `CLAUDE_CODE_ENTRYPOINT` (`claude-vscode` -> "VS Code"); only that mapping has been seen.

## Decisions the user made or implied
- Milestones fire **every 5 %** (5, 10 … 100), once per window; a jump gives one popup for the highest step.
- Look and layout follow Open Island's (spec in `docs/design.md`); font sizes and layout should be taken from it.
- Commit by feature and step; prefer small separate commits. The repo now has a GitHub remote; push only when asked.
- All agent-specific code goes in its own integration library (session 3); Core stays agent-neutral.
- Usage polling needs consent; the endpoint is unofficial. The token is read-only: never refresh, never print, never persist.
- Do not touch the real `~/.claude/settings.json` outside the installer. Do not kill the user's running app.

## Open questions for the user
1. **Licence.** Open Island (repo `Octane0411/open-vibe-island`) is GPL v3 and says "fork it, mod it, ship your own version". Reusing its code, `design/` bundles or `Assets/Brand` means this project must be GPL-3.0 too, and its name/logo need the maintainers' say-so. Nothing has been copied (only `demo.gif` was viewed in a scratch folder to compare proportions). Ask which licence for this project.
2. **Smart App Control is enforced on this machine.** Unsigned binaries get blocked unpredictably (the test DLL once, the app once; the forwarder never). Choices: sign with a trusted certificate (self-signed does not work; Microsoft's Artifact Signing is one route, price and eligibility unverified) or turn SAC off in Windows Security (Microsoft says it can be re-enabled on current Windows; verify). Diagnose blocks in Event Viewer: `Microsoft-Windows-CodeIntegrity/Operational`, events 3076/3077. Decide before WP6 puts the forwarder in the hook path: a blocked forwarder means a silent island.
3. ~~Git identity~~ (confirmed).
4. ~~Scope across projects~~ (decided: user level + per-project Show / Ignore). Original note: Hooks live in Claude Code settings files: user-level `~/.claude/settings.json` applies to every project; `.claude/settings.json` and `.claude/settings.local.json` in a project folder apply to that project only (general Claude Code knowledge, not yet verified here). The plan installs at **user level**, so every session in every project reaches the one island, distinguished by `session_id` and shown by folder name. To limit it to chosen projects, install into those projects' settings instead, or add a "mute project" setting in WP7 (not built).

## Gotchas learned the hard way
- Windows PowerShell 5.1 is the default shell: `Set-Content -Encoding utf8` writes a BOM, `Get-Content` misreads UTF-8 without one, `&&` does not exist. `-File script.ps1 -Param 1,2,3` passes ONE string "1,2,3" (not an array): take a string and split it.
- Screenshot scripts: a plain PowerShell process is DPI-unaware, so `CopyFromScreen` coordinates are scaled (1707 px wide on this 2560 px / 150 % screen). Capture the whole width instead of guessing a centred crop. `smoke-overlay.ps1` sets per-monitor-v2 awareness itself so UI Automation and the mouse APIs agree.
- In bash heredocs and `node -e "..."`, backticks run commands. Use quoted heredocs (`<<'EOF'`) and avoid backticks in unquoted ones.
- `NamedPipeClientStream.ConnectAsync(timeout)` waits the whole timeout when the pipe is missing (250 ms per hook). `IslandClient` checks `\\.\pipe\` first. Do not remove that.
- WinForms analyzer WFAC010 rejects manifest DPI settings; the App csproj suppresses it (WPF app, WinForms only for the tray).
- WPF + WinForms: the csproj removes the implicit `System.Windows.Forms` / `System.Drawing` usings to avoid type clashes. `HttpClient` needs `using System.Net.Http;` in the App. Animation types are in `System.Windows.Media.Animation`.
- The app process owns two top-level windows (control panel + island): UI Automation scripts must select by title ("Claude Code Island - panel" vs "Claude Code Island").
- Smoke scripts refuse to run while the user's installed island runs. Close it with the quit signal (`Local\ClaudeCodeIsland-Quit`, as `install-local.ps1` does), run them, then `install-local.ps1` to restart it. Never kill the process.
- Benchmarks or `simulate` against the live pipe show up on the user's island (a payload without `cwd` is let through as a session). Point them at a fake pipe with `ISLAND_PIPE_NAME`.
- Hook payloads: `PreToolUse` fires *before* `PermissionRequest`; `Notification(permission_prompt)` arrives ~6 s after the request and is a duplicate (the store de-duplicates it); a hook-issued deny fires neither `PostToolUse` nor `PermissionDenied`.
- The usage endpoint's `resets_at` has different fractional seconds on every call; milestone keys round to the minute.
- `git` warns about LF/CRLF (autocrlf=true). Harmless.

## Suggested first 10 minutes of the next session
1. `git log --oneline`, `git status`, `dotnet build ClaudeCodeIsland.sln`, `dotnet test ClaudeCodeIsland.sln`.
2. Ask the user the blocking questions (licence, signing/SAC) and whether the identity is right.
3. Start WP6 (installer) test-first against temp copies of a settings file.
