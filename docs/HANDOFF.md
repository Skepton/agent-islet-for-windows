# Handoff (end of session 3, 2026-10-14)

Read this, then `CLAUDE.md` (ground rules) and `PLAN.md` (architecture; status table in §8). Evidence for Claude Code behaviour is in `docs/findings.md`; the look reference is `docs/design.md`.

## What it is
A Windows overlay ("island") that drops down from the top of the screen when a coding agent needs the user: permission requests (answer No / Yes / Always Allow from the island), questions, waiting-for-input, new-project approval, and usage milestones for the 5-hour and 7-day limits. Claude Code is the only agent so far; all agent-specific code lives in `src/Island.ClaudeCode`. C# / .NET 8 / WPF.

**The name is undecided.** The repo and app are still called "Claude Code Island" (`ClaudeCodeIsland.sln`, `ClaudeCodeIsland.exe`). The user rejected "AI² / AI Squared" and suggested something like **"Agent Island for Windows"**. Ask before renaming anything.

## Right now
- Repo `main` at `8d9cade`, working tree clean. This session's commits: `5ed3497` (call matching, questions, remember labels, event trace), `e8a579a` (control panel, collapsed pill, dark tray menu, quit event), `8d9cade` (docs).
- **Pushed status is unverified.** The local `origin/main` ref points at `8d9cade`, but no push of those three commits was run in this session. Run `git fetch` and `git status -sb` before saying what is on GitHub.
- The AI² rename was **discarded** from the working tree (`git reset --hard`). It is saved as `ai-squared-rename.patch` in the session scratchpad (`C:\Users\xeino\AppData\Local\Temp\claude\c--Users-xeino-Documents-Projects-claude-code-island\884d5cde-47bc-445d-9d5a-26d67ee0a8b1\scratchpad\`). It is mostly obsolete for the new name, but see "Rename mechanics" below.
- Tests at `8d9cade`: 309 pass (Core 155, Claude Code 154). `smoke-app` and `smoke-app-ui` pass. `smoke-overlay` (moves the mouse) and `smoke-usage` (one live API call) have not been run since the session-3 changes.
- The user's installed island is the `8d9cade` build in `%LOCALAPPDATA%\ClaudeCodeIsland\app`, running, pipe listening. Its 8 hooks in `~/.claude/settings.json` point at `%LOCALAPPDATA%\ClaudeCodeIsland\bin\island-hook.exe`. Project choices are in `%LOCALAPPDATA%\ClaudeCodeIsland\projects.json`.
- `%LOCALAPPDATA%\AISquared\projects.json` is a copy made by the rename test run (`File.Copy` keeps timestamps). Harmless; delete when convenient.

## Session 4 (2026-10-16), all uncommitted
- **Usage setting + thin bar.** `config.json` (`ConfigStore`, `IslandConfig`: usage on/off, consent time, bar mode) and a panel **Usage** tab (switch with consent text, "thin bar" / "only when it changes"). `--poll-usage` now just writes consent to `config.json`. The idle island is an 8 px bar with one gauge per limit; `UsageReveal` (Core, tested) shows the numbers for 6 s when whole percentages change and while the pointer rests on the bar. The bar is click-through, so hover is found by polling the cursor (`OverlayController.OnHoverTick`). A card always replaces the strip. Checked live with `--demo` (new Quiet and UsageCreep scenes) and DPI-aware screenshots.
- **Subagent cards** are named after the project folder (`AgentEvent.ProjectName`).
- **Card "disappearing" report (user):** the island shrank to its pill after 5 s (2 s after the pointer left). Now 20 s / 8 s (`IslandCollapse.After`, `LeaveGrace`), and the debug log says "island shrank to the pill". Not confirmed as the user's cause: if it happens again, the Debug tab shows `permission closed (outcome)` for a real clear. Second suspect: `ToolProceeded` with a null call key releases every pending request and notice.
- **App icon:** drawn in code by `scripts/make-icon.ps1` (orange tile, black island hanging from the top edge) into `src/Island.App/Assets/island.ico` and `docs/images/icon-preview.png`. Used for the exe (`ApplicationIcon`), the panel window and the tray (`TrayIconHost.LoadIcon`, sized for the display; falls back to the stock icon). Re-run the script to change the art. An "attention" badge variant is not done.
- **"Show all N sessions" footer removed** from the island (user's call; the panel's Sessions tab has it). `OverlayController` no longer takes a `showAll` action and `SessionCountOverride` is gone.
- Tests: 337 (Core 183, Claude Code 154). Installed island is the repo's Debug build run with `--tray` (not `install-local.ps1`).
- Distribution (asked by user): portable self-contained exe on GitHub Releases, in-app update check against the Releases API, first-run hook setup. Blocker: Smart App Control / SmartScreen with an unsigned exe. See the chat answer; nothing built yet.

## Next, in the user's order
1. **Name, logo and banner for the README.** Requested; not started. Agree the product name with the user first (candidate: "Agent Island for Windows"). Then:
   - **Logo:** a simple SVG of the island pill: black rounded shape, orange `#E58C36` accent (as in `Theme.xaml`). Original art only; do not copy Open Island's assets (licence question below).
   - **Banner:** HTML/CSS rendered to PNG with headless Edge (`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`, `--headless --screenshot`). Save both under `docs/images/` and reference them from `README.md`.
2. **Panel Island and Usage tabs:** island position (top centre / left / right; follow the active monitor or always the primary), card timings, and a real usage on/off switch with consent text (replacing `--poll-usage` / `-PollUsage`). Needs `config.json` in the data folder (PLAN §4.8).
3. **Click-to-focus:** clicking a card's body (not its buttons) brings the session's window forward. The forwarder already sends `CLAUDE_PID`; add `VSCODE_PID` to the env mapping. VS Code: pick the Code.exe window whose title contains the project folder name. Terminals: walk parent processes up to one with a window. Keep the window choice a pure, tested function in Core; the Win32 calls go in the App. Check that Windows lets a no-activate window bring another window forward.
4. **Verify live:** approving a request in VS Code clears the island card (call-key matching, findings L6). Re-run `smoke-overlay` and `smoke-usage` (ask the user first: they move the mouse / call the API).
5. ~~Subagent cards are named after their worktree folder~~ Done (uncommitted, session 4): the hub sets `AgentEvent.ProjectName` from `CLAUDE_PROJECT_DIR`; `SessionStore.DisplayNameFor` prefers it. Not yet checked live; the installed island is still the `8d9cade` build.
6. Re-measure forwarder latency on a quiet machine (findings L8).

## Rename mechanics (for whichever name is chosen)
- Names live in `src/Island.Core/Product.cs` (`Name`, `Id`, `OldId`). Pipe name: `IslandPipe.cs`. Mutex and quit-event names: `App.xaml.cs`. Exe name: `AssemblyName` in `Island.App.csproj`. Window titles: `IslandWindow.xaml`, `PanelWindow.xaml`, `TrayIconHost.cs`.
- The data folder changes too: `IslandPaths.MigrateFrom` copies `projects.json` and `state.json` once from the old folder (tested).
- `scripts/install-local.ps1` must close the old-named island through its quit event and point the hooks at the new folder. The installer already handles a moved exe (tested).
- Smoke scripts and UI Automation titles must match the new names. Write non-ASCII in `.ps1` as `[char]0xNN`.

## Open decisions (ask the user)
- **Name** (see above).
- **Licence.** Open Island is GPL v3. Reusing its code, design bundles or brand assets would force GPL-3.0 here, and the name/brand need the maintainers' say-so. Nothing has been copied. Still undecided.
- **Smart App Control** is enforced on this machine; unsigned binaries are sometimes blocked. Options: trusted signing, or turn SAC off. Decide before packaging.

## Decisions and preferences
- Commit by feature, in small commits. Commit and push only when the user asks.
- Commit trailer from now on: `Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>` (the user switched to Haiku 5.5 with `/model haiku`). Earlier commits carry the Opus 5.5 trailer; leave history alone.
- Never kill the user's running island. Close it through its quit event (`Local\ClaudeCodeIsland-Quit` now) or from the tray.
- Don't touch the real `~/.claude/settings.json` except through the installer. The user's island hooks in it are intentional (see CLAUDE.md).
- All new UI matches the island: black, rounded, orange accent, Segoe UI.
- Milestones fire every 5 %, once per window. Usage polling needs explicit consent; the token is read-only, never printed, persisted or refreshed.
- Smoke scripts refuse to run while the installed island runs. Close it (quit event or tray), run them, then reinstall with `install-local.ps1`.

## Architecture (at 8d9cade)
```
src/Island.Core        agent-neutral: IAgentIntegration + AgentEvents + PermissionVerdict, sessions, hub + transport,
                       projects (ProjectPolicy), usage engine (MilestoneEngine, UsageMonitor, PollSchedule),
                       overlay: OverlayState (cards, IslandCollapse), CardPresenter, UsageFormat, TextUtil, AtomicFile. No UI.
src/Island.ClaudeCode  ClaudeCodeAgent (payload → events, verdict → hook output), ClaudeCodeHook (forwarder side),
                       HookPayload, PermissionDecision, SuggestionChoice, ToolSummary (CallKey), QuestionText,
                       Setup/ (HookSettings, SettingsFile), Usage/ (OAuth credentials, source, parser), ClaudePaths
src/Island.Hook        island-hook.exe: `hook` (stdin), `simulate <file> [secs]`, `install|uninstall [--settings] [--dry-run]`.
                       Hook mode always exits 0 and stays silent on error.
src/Island.App         WPF app: App.xaml.cs (single instance, quit event, tray, hub + server, agents, flags),
                       PanelWindow + PanelViewModel + ProjectsViewModel (dark control panel: Sessions / Projects / Debug),
                       IslandWindow (island + collapsed pill) + OverlayViewModels + OverlayController + DemoDriver,
                       TrayIconHost (dark menu, left-click = panel), Theme.xaml (shared styles), NativeMethods.cs
tests/                 Island.Core.Tests (TestAgent), Island.ClaudeCode.Tests + tests/fixtures/{hooks,usage} (sanitised payloads)
scripts/               publish-hook, bench-hook, install-local, smoke-app, smoke-app-ui, smoke-overlay, smoke-usage
```
Key flow: Claude Code hook → `island-hook.exe` (`ClaudeCodeHook`) → named pipe `\\.\pipe\claude-code-island-<SID>` → `IslandServer` → `IslandHub` (project gate) → `ClaudeCodeAgent.Translate` → `SessionStore` → effects → `OverlayController` / `PanelViewModel` → windows. A permission request keeps its pipe open until `IslandHub.Answer(verdict)`; the broker asks the owning integration to encode the verdict, and the forwarder prints it verbatim. A null verdict means "no decision", so Claude Code shows its own prompt.

## Build, test, run
```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')  # if dotnet is not found
dotnet build ClaudeCodeIsland.sln
dotnet test ClaudeCodeIsland.sln
powershell -File scripts\publish-hook.ps1          # artifacts\hook\island-hook.exe (needed by the smoke scripts)
powershell -File scripts\smoke-app.ps1             # real app + real forwarder
powershell -File scripts\smoke-app-ui.ps1          # clicks the control panel's buttons via UI Automation
powershell -File scripts\smoke-overlay.ps1         # focus safety + REAL mouse clicks (moves the mouse)
powershell -File scripts\smoke-usage.ps1           # ONE live usage poll against a throwaway data folder
powershell -File scripts\install-local.ps1         # everyday install: publish to %LOCALAPPDATA%, install hooks, start (closes the running one first)
dotnet run --project src/Island.App -- --allow-all-projects   # flags: --tray, --demo, --poll-usage, --allow-all-projects
.\artifacts\hook\island-hook.exe simulate tests\fixtures\hooks\PermissionRequest.with-suggestions.json 120
```
Run smoke scripts as `powershell -NoProfile -ExecutionPolicy Bypass -File ...` if script execution is blocked.

## Gotchas learned the hard way
- Windows PowerShell 5.1 reads BOM-less `.ps1` files as ANSI: write non-ASCII as `[char]0xB2` etc.
- A bulk replace in PowerShell (read and write with `ReadAllText`/`WriteAllText`) rewrote every line of three smoke scripts. Edit `.ps1` files with the Edit tool.
- `git reset --hard` discards uncommitted work. Save a patch first (`git diff --cached --binary > file`).
- `Remove-Item` on a path built from a variable was refused as protected in this environment; use fixed paths.
- `install-local.ps1` refuses to run while a permission request waits on the installed forwarder (answer it in Claude Code first).
- The app owns two top-level windows; UI Automation must select by title ("AI² - panel" style names after a rename; "Claude Code Island - panel" at this commit).
- Benchmarks or `simulate` against the live pipe show up on the user's island (a payload without `cwd` is let through). Point them at a fake pipe with `ISLAND_PIPE_NAME`.
- `NamedPipeClientStream.ConnectAsync(timeout)` waits the whole timeout when the pipe is missing; `IslandClient` checks `\\.\pipe\` first. Keep that.
- WinForms analyzer WFAC010 rejects manifest DPI settings; the App csproj suppresses it.
- WPF + WinForms: the csproj removes the implicit `System.Windows.Forms` / `System.Drawing` usings; `System.IO` must be imported explicitly in the App.
- Protocol v2: an old `island-hook.exe` and a new app do not understand each other. That fails open (silence). Republish both together (`install-local.ps1` does).
- Hook payloads: `PreToolUse` fires before `PermissionRequest`; `Notification(permission_prompt)` arrives ~6 s after the request and duplicates it; a hook-issued deny fires neither `PostToolUse` nor `PermissionDenied`; `PermissionRequest` has no `tool_use_id` (match via `ToolSummary.CallKey`).
- The usage endpoint's `resets_at` has different fractional seconds on every call; milestone keys round to the minute.
- `git` warns about LF/CRLF (autocrlf=true). Harmless.

## Suggested first 10 minutes of the next session
1. `git fetch`, `git status -sb`, `git log --oneline -5`. Confirm the installed island is running.
2. `dotnet build ClaudeCodeIsland.sln` and `dotnet test ClaudeCodeIsland.sln` (expect 309 passing).
3. Ask the user for the product name and the licence answer, then do the logo and banner.
