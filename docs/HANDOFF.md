# Handoff (end of session 1)

Read this, then `CLAUDE.md` (ground rules) and `PLAN.md` (architecture, work packages, status). Evidence for every Claude Code behaviour we rely on is in `docs/findings.md`; the target look is in `docs/design.md`.

## What this is
A Windows-only, Claude Code-only "island" that drops down from the top-centre of the screen when Claude Code needs the user (permission approval, waiting for input), shows usage milestones for the 5-hour and 7-day limits, and lets the user approve or deny from the island. C# / .NET 8 / WPF. Inspired by Open Island (macOS).

## State right now
- Branch `main`, **11 local commits (the last two are the plan-status update and this handoff), nothing pushed, working tree clean**. Git identity is repo-local (`xeino <xeinon@gmail.com>`, guessed from the Windows username and account email; change with `git config user.name/user.email` and rewrite if wrong). Commits carry the `Co-Authored-By: Claude Sonnet 5.5` trailer.
- Every commit builds on its own (checked in isolated worktrees). 170 tests pass. Both app smoke scripts pass.
- Commit order = feature order: conventions, docs, core contracts + state machine, pipe transport + hub, forwarder, usage + milestones, overlay card queue, app shell, overlay foundations, plan status, handoff. `git log --oneline` reads as the story.
- **Nothing is installed in the user's Claude Code settings.** The Phase 0 spike hooks were removed. The installer (WP6) does not exist, so no real Claude Code session reaches the app yet.

## Build, test, run
```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')  # if dotnet is not found
dotnet build ClaudeCodeIsland.sln
dotnet test ClaudeCodeIsland.sln
powershell -File scripts\publish-hook.ps1          # artifacts\hook\island-hook.exe (needed by the smoke scripts)
powershell -File scripts\smoke-app.ps1             # launches the real app, drives it with the real forwarder
powershell -File scripts\smoke-app-ui.ps1          # clicks the real buttons via UI Automation (briefly shows a window)
powershell -File scripts\smoke-usage.ps1           # ONE live usage poll against a throwaway data folder
dotnet run --project src/Island.App                # debug window; flags: --tray (start hidden), --poll-usage (opt in to usage polling)
.\artifacts\hook\island-hook.exe simulate tests\fixtures\hooks\PermissionRequest.with-suggestions.json 120   # card appears; click it; JSON prints
```
All smoke scripts refuse to run if an island instance is already running (they never kill the user's). If the user has the app open, the exe is locked and `dotnet build` of the App fails: compile into a scratch folder with `-o <dir>` instead, or ask them to quit it from the tray.

## Map
```
src/Island.Core   hooks/protocol/permissions/sessions (WP1), hub + transport (pipe server/client), usage + milestones (WP5),
                  overlay card queue + usage formatting. No UI. net8.0-windows, trim-analyzed.
src/Island.Hook   island-hook.exe forwarder: `hook` (stdin) and `simulate <file> [secs]`. Always exit 0, silent on error.
src/Island.App    WPF app: App.xaml.cs (single instance, tray, hosts server + hub, --poll-usage), debug window, Theme.xaml,
                  app.manifest (per-monitor DPI), NativeMethods.cs (unused so far).
tests/            xUnit (170) + tests/fixtures/{hooks,usage} (real, sanitised payloads)
scripts/          publish-hook, bench-hook, smoke-app, smoke-app-ui, smoke-usage
```
Key flow: Claude Code hook -> `island-hook.exe` -> named pipe `\\.\pipe\claude-code-island-<SID>` -> `IslandServer` -> `IslandHub` -> `SessionStore` (state machine) -> effects -> UI. A PermissionRequest keeps its pipe connection open until `IslandHub.Answer` is called; null means "no decision", so Claude Code shows its own prompt.

## What is left, in order

### 1. Finish WP4 (the visible island). Already done: `OverlayState`, `UsageFormat`, theme, manifest, Win32 helpers.
1. **Card text in Core, tested.** A small `CardPresenter` (pure, in Island.Core) that turns an `OverlayCard` into display text: title `project · last prompt`, `You: …`, attention line (`Claude wants to run Bash.` for Bash/PowerShell, otherwise `use <Tool>`), command prefix (`$` Bash, `PS>` PowerShell, none otherwise), chips (`Claude Code`, `ClientApp`, age via `UsageFormat.Age`), button labels (`addRules` -> `Always Allow (<tool>)`, `addDirectories` -> `Allow + add folder`, else the suggestion's `Label`), usage card title/progress/level. WPF then only binds. There is no App test project, so keep logic out of the App.
2. **View-models in the App**: `OverlayViewModel` (INotifyPropertyChanged; current card, `1 of N`, strip items, footer `Show all N sessions`) and card VMs (permission, notice, usage). Buttons: No (deny), Yes (allow), then one per suggestion; plus an "Answer in terminal" text link. Equal-width `UniformGrid`.
3. **`IslandWindow.xaml(.cs)`**: borderless, transparent, topmost, `ShowActivated=False`, `ShowInTaskbar=False`, width 480 DIP, `SizeToContent=Height`, panel `CornerRadius="0,0,20,20"`. In `OnSourceInitialized` set `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` and hook `WM_MOUSEACTIVATE` -> `MA_NOACTIVATE`. Toggle `WS_EX_TRANSPARENT` (click-through) when only the usage strip shows. Place with `SetWindowPos(HWND_TOPMOST, …, SWP_NOSIZE|SWP_NOACTIVATE)` at the top-centre of the work area of the monitor containing the foreground window (`MonitorFromWindow` + `GetMonitorInfo` + `GetDpiForMonitor`). Slide + fade in/out ~200 ms. DataTemplates per card VM; styles come from `Theme.xaml` (do not hard-code colours). Values: `docs/design.md`.
4. **`OverlayController`** (App): owns `OverlayState`, the VM and the window. `OnEffects` (from the hub's present callback), `OnUsage` (from the usage monitor callback), a 500 ms tick (`state.Tick`; refresh ages every ~15 s), a 2 s full-screen check (`NativeMethods.IsFullScreenAppRunning` -> `state.SuppressNonCritical`). Answers: ids starting `demo-` are handled locally, others go to `hub.Answer`; log every answer through the debug VM (add a public `LogLine`). Show the usage strip whenever usage data exists. Wire it in `App.xaml.cs` next to the debug VM; `Show all N sessions` should call `ShowWindow()`.
5. **`--demo` mode**: a `DemoDriver` that cycles fake cards every few seconds: permission (with an addRules suggestion), needs input, finished (long reply), milestone 35 %, milestone 90 % (sticky), three sessions (pager + footer). Use it to tune the look.
6. **Focus-safety smoke test `scripts/smoke-overlay.ps1`** (the central risk, PLAN §5.5): record the current foreground window, launch the app `--tray`, send a PermissionRequest through `island-hook simulate` in a background job, wait for the island window (UI Automation, by process id), assert the foreground window did not change, assert `WS_EX_NOACTIVATE` is set, find the "Yes" button, confirm `AutomationElement.FromPoint(centre)` is that button, do a **real mouse click** (`SetCursorPos` + `mouse_event`, restore the cursor), then assert the forwarder printed the allow decision and the foreground window is still unchanged. Tell the user before running it: it moves the mouse briefly.
7. **Tune visually** against the reference (download `demo.gif` from the Open Island README into a scratch folder, extract frames ~145, 194, 291 with System.Drawing, compare side by side). The numbers in `docs/design.md` are ±15 % estimates.
8. Unverified and worth checking on this machine: click-through strip over title bars, placement with a top taskbar and on mixed-DPI monitors, `Segoe UI Variable Text` availability, whether buttons really take clicks under `WS_EX_NOACTIVATE`.

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
- Commit by feature and step, **local only, nothing pushed**. Prefer small separate commits.
- Usage polling needs consent; the endpoint is unofficial. The token is read-only: never refresh, never print, never persist.
- Do not touch the real `~/.claude/settings.json` outside the installer. Do not kill the user's running app.

## Open questions for the user
1. **Licence.** Open Island is GPL v3 and says "fork it, mod it, ship your own version". Reusing its code, `design/` bundles or `Assets/Brand` means this project must be GPL-3.0 too, and its name/logo need the maintainers' say-so. The user said the Open Island people "want to implement a Windows version eventually". Nothing has been copied. Ask which licence, then inspect those folders. Its Swift/AppKit code is not reusable in WPF; assets and design values might be.
2. **Smart App Control is enforced on this machine.** Unsigned binaries get blocked unpredictably (the test DLL once, the app once; the forwarder never). Choices: sign with a trusted certificate (self-signed does not work; Microsoft's Artifact Signing is one route, price and eligibility unverified) or turn SAC off in Windows Security (Microsoft says it can be re-enabled on current Windows; verify). Diagnose blocks in Event Viewer: `Microsoft-Windows-CodeIntegrity/Operational`, events 3076/3077. Decide before WP6 puts the forwarder in the hook path: a blocked forwarder means a silent island.
3. **Git identity** (see above).
4. **Scope across projects (the user asked how to hook this up to their other parallel project).** Hooks live in Claude Code settings files: user-level `~/.claude/settings.json` applies to every project; `.claude/settings.json` and `.claude/settings.local.json` in a project folder apply to that project only (this scope behaviour is from general Claude Code knowledge, not yet verified here). The plan installs at **user level**, so every session in every project reaches the one island, distinguished by `session_id` and shown by folder name; nothing per-project is needed. The pipe is per Windows user. To limit it to chosen projects, either install into those projects' settings instead, or add a "mute project" setting in WP7 (not built). The other session seen earlier was in `soccer-fk\research`.

## Gotchas learned the hard way
- Windows PowerShell 5.1 is the default shell: `Set-Content -Encoding utf8` writes a BOM, `Get-Content` misreads UTF-8 without one, `&&` does not exist.
- In bash heredocs and `node -e "..."`, backticks run commands. Use quoted heredocs (`<<'EOF'`) and avoid backticks in unquoted ones.
- `NamedPipeClientStream.ConnectAsync(timeout)` waits the whole timeout when the pipe is missing (250 ms per hook). `IslandClient` checks `\\.\pipe\` first. Do not remove that.
- WinForms analyzer WFAC010 rejects manifest DPI settings; the App csproj suppresses it (WPF app, WinForms only for the tray).
- WPF + WinForms: the csproj removes the implicit `System.Windows.Forms` / `System.Drawing` usings to avoid type clashes. `HttpClient` needs `using System.Net.Http;` in the App.
- Hook payloads: `PreToolUse` fires *before* `PermissionRequest`; `Notification(permission_prompt)` arrives ~6 s after the request and is a duplicate (the store de-duplicates it); a hook-issued deny fires neither `PostToolUse` nor `PermissionDenied`.
- The usage endpoint's `resets_at` has different fractional seconds on every call; milestone keys round to the minute.
- `git` warns about LF/CRLF (autocrlf=true). Harmless.
- The WebFetch/WebSearch results about Open Island came from its README and a 4.5 MB `demo.gif`; no still screenshots exist in the repo.

## Suggested first 10 minutes of the next session
1. `git log --oneline`, `git status`, `dotnet build ClaudeCodeIsland.sln`, `dotnet test ClaudeCodeIsland.sln`.
2. Ask the user the two blocking questions (licence, signing/SAC) and whether the identity is right.
3. Start WP4 step 1 (CardPresenter + tests), then steps 2-6 above. Commit after each step.
