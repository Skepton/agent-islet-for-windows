# Handoff (end of session 4, 2026-10-16)

Read this, then `CLAUDE.md` (ground rules) and `PLAN.md` (architecture; status table in §8). Evidence for Claude Code behaviour is in `docs/findings.md`; the look reference is `docs/design.md`.

## What it is
A Windows overlay ("island") that drops down from the top of the screen when a coding agent needs the user: permission requests (answer No / Yes / Always Allow from the island), questions, waiting-for-input, new-project approval, and usage milestones for the 5-hour and 7-day limits. Claude Code is the only agent so far; all agent-specific code lives in `src/Island.ClaudeCode`. C# / .NET 8 / WPF.

**Name: Agent Islet** (decided 2026-10-16; written "Agent Islet" in the UI and README, `agent-islet-for-windows` as the repo, "for Windows" in the tagline). It avoids "Claude" in the name (Anthropic's trademark guidelines bar implying affiliation) and "Agent Island" (an existing app, agent-island.dev). The README says it is not affiliated with Anthropic or Open Island. Tray, window titles, README, docs and exe product metadata (`Directory.Build.props`) use the new name.

**Still on the old name (deliberately):** the identifiers `ClaudeCodeIsland.sln`, `ClaudeCodeIsland.exe` (`AssemblyName`), the `%LOCALAPPDATA%\ClaudeCodeIsland` data folder, the pipe name (`IslandPipe.cs`) and the mutex and quit-event names (`App.xaml.cs`). Changing them breaks the link between the installed hooks and the app until `install-local.ps1` is re-run, so do it as one step with the user's OK (see "Rename mechanics").

## Right now
- Repo: `Skepton/agent-islet-for-windows` (public; renamed from `win-ai-squared`, which still redirects). Check `git fetch` and `git status -sb` before saying what is on GitHub.
- Tests: 337 pass (Core 183, Claude Code 154). `smoke-app` and `smoke-app-ui` were last run before session 4 (their window-title lookups now say "Agent Islet"); `smoke-overlay` (moves the mouse) and `smoke-usage` (one live API call) have not been run since the session-3 changes.
- The user's island is the repo's Debug build run with `--tray`. The 8 hooks in `~/.claude/settings.json` still point at the older installed forwarder in `%LOCALAPPDATA%\ClaudeCodeIsland\bin\island-hook.exe` (protocol v2, still compatible). Project choices are in `%LOCALAPPDATA%\ClaudeCodeIsland\projects.json`.

## Session 4 (2026-10-16)
- **Usage setting + thin bar.** `config.json` (`ConfigStore`, `IslandConfig`: usage on/off, consent time, bar mode) and a panel **Usage** tab (switch with consent text, "thin bar" / "only when it changes"). `--poll-usage` now just writes consent to `config.json`. The idle island is an 8 px bar with one gauge per limit; `UsageReveal` (Core, tested) shows the numbers for 6 s when whole percentages change and while the pointer rests on the bar. The bar is click-through, so hover is found by polling the cursor (`OverlayController.OnHoverTick`). A card always replaces the strip. Checked live with `--demo` (new Quiet and UsageCreep scenes) and DPI-aware screenshots.
- **Subagent cards** are named after the project folder (`AgentEvent.ProjectName`).
- **Card "disappearing" report (user):** the island shrank to its pill after 5 s (2 s after the pointer left). Now 20 s / 8 s (`IslandCollapse.After`, `LeaveGrace`), and the debug log says "island shrank to the pill". Not confirmed as the user's cause: if it happens again, the Debug tab shows `permission closed (outcome)` for a real clear. Second suspect: `ToolProceeded` with a null call key releases every pending request and notice.
- **App icon:** drawn in code by `scripts/make-icon.ps1` (orange tile, black island hanging from the top edge) into `src/Island.App/Assets/island.ico` and `docs/images/icon-preview.png`. Used for the exe (`ApplicationIcon`), the panel window and the tray (`TrayIconHost.LoadIcon`, sized for the display; falls back to the stock icon). Re-run the script to change the art. An "attention" badge variant is not done.
- **"Show all N sessions" footer removed** from the island (user's call; the panel's Sessions tab has it). `OverlayController` no longer takes a `showAll` action and `SessionCountOverride` is gone.
- Tests: 337 (Core 183, Claude Code 154). Installed island is the repo's Debug build run with `--tray` (not `install-local.ps1`).
- Distribution (asked by user): portable self-contained exe on GitHub Releases, in-app update check against the Releases API, first-run hook setup. Blocker: Smart App Control / SmartScreen with an unsigned exe. See the chat answer; nothing built yet.

## Next, in the user's order
1. **Release and signing** (see `docs/distribution.md`): release workflow, first-run "Set up" button and "Remove hooks", embedded forwarder, update check, first unsigned release, then the user applies to SignPath. Also a README banner (HTML/CSS rendered to PNG with headless Edge, `--headless --screenshot`; original art only, nothing from Open Island). The icon is already in the README (`docs/images/icon.png`).
   - Rename the internal identifiers first (see "Rename mechanics"), so the signed exe is `AgentIslet.exe`.
2. **Panel Island and Usage tabs:** island position (top centre / left / right; follow the active monitor or always the primary), card timings, and a real usage on/off switch with consent text (replacing `--poll-usage` / `-PollUsage`). Needs `config.json` in the data folder (PLAN §4.8).
3. **Click-to-focus:** clicking a card's body (not its buttons) brings the session's window forward. The forwarder already sends `CLAUDE_PID`; add `VSCODE_PID` to the env mapping. VS Code: pick the Code.exe window whose title contains the project folder name. Terminals: walk parent processes up to one with a window. Keep the window choice a pure, tested function in Core; the Win32 calls go in the App. Check that Windows lets a no-activate window bring another window forward.
4. **Verify live:** approving a request in VS Code clears the island card (call-key matching, findings L6). Re-run `smoke-overlay` and `smoke-usage` (ask the user first: they move the mouse / call the API).
5. ~~Subagent cards are named after their worktree folder~~ Done in session 4 (`AgentEvent.ProjectName`, from `CLAUDE_PROJECT_DIR`). Not yet checked live with a real subagent.
6. Re-measure forwarder latency on a quiet machine (findings L8).

## Rename mechanics (the identifiers still on the old name)
- Pipe name: `src/Island.Core/Protocol/IslandPipe.cs` (`claude-code-island-<SID>`). Mutex and quit-event names: `App.xaml.cs`. Exe name: `AssemblyName` in `Island.App.csproj`. Data folder: `IslandPaths.DataDir` (in `MilestoneStateStore.cs`). Solution file: `ClaudeCodeIsland.sln`.
- Add a one-time migration that copies `projects.json`, `state.json` and `config.json` from the old data folder to the new one (write a test).
- `scripts/install-local.ps1` must close the old-named island through its old quit event, publish to the new folder, and re-point the hooks (the installer already handles a moved exe, tested). Until it is re-run, hooks keep talking to the old pipe name, so rename the pipe and re-run it together.
- The smoke scripts, `tests/.../InstallerTests.cs` and the docs mention the old pipe, exe and folder names. Write non-ASCII in `.ps1` as `[char]0xNN`.

## Decisions
- **Licence: GPL-3.0** (2026-10-16), same as Open Island (verified on its repo page). `LICENSE` added. Nothing was copied from Open Island; its name and brand are theirs, so we do not use "Open Island" as our name.
- **Signing: try SignPath Foundation** (2026-10-16). Rules and checklist in `docs/distribution.md`. Applying is the user's step, once there is a public unsigned release.

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
- The app owns two top-level windows; UI Automation must select by title ("Agent Islet" for the island, "Agent Islet - panel" for the panel).
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
