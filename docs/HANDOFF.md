# Handoff (end of session 4, 2026-10-17)

Read this, then `CLAUDE.md` (ground rules), `PLAN.md` (architecture; older status table in §8), `docs/findings.md` (verified Claude Code behaviour, incl. the new http-hook section H1-H8), `docs/distribution.md` (release and signing plan) and `docs/design.md` (the look).

## What it is
**Agent Islet for Windows**: a Windows overlay ("island") that drops down from the top of the screen when a coding agent needs the user: permission requests (answer No / Yes / Always Allow from the island), questions, waiting-for-input, new-project approval, usage milestones, and a thin usage bar when idle. Claude Code is the only agent so far; all agent-specific code lives in `src/Island.ClaudeCode`. C# / .NET 8 / WPF. Licence GPL-3.0. Repo `Skepton/agent-islet-for-windows` (public).

Naming (decided 2026-10-16): the product is "Agent Islet" (capitals and a space in the UI and README), `agent-islet-for-windows` as the repo, `AgentIslet.exe`, `%LOCALAPPDATA%\AgentIslet`. It avoids "Claude" in the name (Anthropic's trademark guidelines bar implying affiliation) and "Agent Island" / "Open Island" (existing projects). "Claude Code" is used only descriptively; the README carries a not-affiliated notice. The forwarder is still `island-hook.exe`.

## State right now
- `main` is **7 commits ahead of `origin/main`** (last pushed: `ff6c336`). Not pushed on purpose; push only when the user says so. Run `git fetch` and `git status -sb` first.
- Tests: **413 pass** (Core 213, Claude Code 200). `smoke-app` and `smoke-app-ui` passed after the rename but **not since the web hooks**; `smoke-overlay` (moves the mouse) and `smoke-usage` (one live API call) have not been run since session 3.
- **The user's island is running** from the repo's Debug build (`src\Island.App\bin\Debug\net8.0-windows\AgentIslet.exe`, started with no flags), so it holds that output folder: `dotnet build` of the App fails with file-in-use errors. Build into a scratch folder instead (`dotnet build src\Island.App\Island.App.csproj -o <scratch>`), or close the island through its quit event `Local\AgentIslet-Quit` (ask first).
- **The user's real hooks are still the old helper kind** (8 `command` entries running `%LOCALAPPDATA%\ClaudeCodeIsland\bin\island-hook.exe`). They work because the app also listens on the pre-rename pipe `claude-code-island-<SID>`. The panel's Claude Code tab shows "connected through the older helper program"; pressing **Switch to the built-in link** (it asks first, backs up the settings file) replaces them with web hooks. Nothing has done that yet. Do not edit `~/.claude/settings.json` any other way.
- `config.json` in `%LOCALAPPDATA%\AgentIslet` holds: usage on/off + consent time + bar mode, `hookPort` (47821) and `hookSecret`. Usage tracking is currently **on** for the user.

## What was done in session 4 (all committed)
1. Cards are named after the project folder (`AgentEvent.ProjectName`), not the worktree.
2. **Usage setting and thin bar:** `config.json` (`ConfigStore`, `IslandConfig`), a Usage tab (switch + consent text, "thin bar" / "only when the numbers change"), `UsageReveal` (Core, tested): an 8-px-tall banner with a label, gauge and percentage per limit; the numbers open for 6 s when whole percentages change or while the pointer rests on the bar. The bar is click-through, so hover is found by polling the cursor (`OverlayController.OnHoverTick`). A card always replaces it. `--poll-usage` only records consent now.
3. Waiting cards stay open 20 s (8 s after the pointer leaves) before shrinking to the pill; the debug log says "island shrank to the pill". The "card disappears" report is **not confirmed** as that cause: next time check the Debug tab for `permission closed (outcome)`. Second suspect: a `ToolProceeded` with a null call key releases every pending request and notice.
4. "Show all N sessions" footer removed. App icon drawn by `scripts/make-icon.ps1` (exe, panel, tray).
5. **Rename to Agent Islet** incl. exe, solution, data folder (`IslandPaths.MigrateLegacyData` copies `projects.json`, `state.json`, `config.json` once from `ClaudeCodeIsland`, never overwriting or deleting), pipe `agent-islet-<SID>`, mutex `Local\AgentIslet`, quit event `Local\AgentIslet-Quit`, product metadata (`Directory.Build.props`: Product "Agent Islet", Version 0.1.0). GPL-3.0 `LICENSE`.
6. **Claude Code tab (Connect / Disconnect)**: `ClaudeCodeSetup`, `SetupViewModel`. Asks in plain words, backs up, edits only our entries.
7. **Built-in web hooks (the user wanted the least setup: no helper exe, no copying):** Claude Code 2.1.301's `http` hook type works (findings H1-H8). `HookHttpServer` (Core; loopback only, `POST /hook/<agent>/<secret>`, `Host` check, `Connection: close`, a waiting request is cancelled when the agent hangs up), `IAgentIntegration.FrameFromHttp`, `IslandHub.HandleHttpAsync`, `HookSettings.InstallHttp` (7 events, no SessionStart), `ProjectFolder.OfWorktree`. Headers pass `CLAUDE_CODE_ENTRYPOINT` and `CLAUDE_PID`; `CLAUDE_PROJECT_DIR` is not available that way.
8. **Screenshots drawn by the app:** `AgentIslet.exe --shots <folder>` renders every view offscreen to PNG (`ShotsDriver`, `IslandWindow.SavePng`); `scripts/make-screenshots.ps1 [-Exe <path>]` wraps it; README uses `docs/images/*.png`. No screen capture.
9. Distribution plan and SignPath rules written down in `docs/distribution.md`; findings H1-H8 recorded.

## Verified vs not verified (web hooks)
- Verified with the real Claude Code: events (`UserPromptSubmit`, `PostToolUse`, `Stop`, `PermissionRequest`) arrive over http; a deny answer makes Claude refuse the tool (findings H3); header interpolation works (H8); the app raises the card for a real `PermissionRequest(Write)`, holds the request, and returns the deny JSON when No is pressed (`post-test` against the real endpoint).
- **Not verified:** the Yes and "Always Allow" answers and `AskUserQuestion` over http with real Claude; latency compared with the 50 ms forwarder; behaviour when the port is closed mid-request; a real subagent's `cwd` (the `.claude\worktrees\<name>` pattern is inferred); Claude Code versions older than 2.1.301 (the minimum that supports `http` hooks is unknown); the app being the foreground-less case; managed policies that restrict hook URLs.

## Next steps (in priority order)
1. **Switch the user to the built-in link and check it live:** ask them to press "Switch to the built-in link", then confirm cards arrive, answer a real request from the island with Yes, No and Always Allow, and a question. Fix whatever differs. (I cannot drive the UI safely: no SendKeys.)
2. **Update the smoke scripts for http:** add an http path to `smoke-app.ps1` (read port and secret from the throwaway `config.json`, POST a fixture, press the card's button through UI Automation `InvokePattern`, check the response). Keep the pipe checks while the forwarder exists. Use `ISLAND_INSTANCE_NAME`, `ISLAND_PIPE_NAME`, `ISLAND_DATA_DIR` so tests run beside the user's island.
3. **Retire the forwarder from the release path:** decide whether `island-hook.exe`, the pipe, `simulate` and `install-local.ps1` stay as a dev tool. `simulate` could post over http.
4. **Release:** GitHub Actions workflow on a version tag (self-contained single-file win-x64 `AgentIslet.exe`, SHA-256, GitHub Release); README download section; first unsigned release; then the user applies to SignPath Foundation (rules and checklist in `docs/distribution.md`). Add the "Code signing policy" section only after acceptance.
5. **Update check** (opt-in): GitHub Releases API, verify the checksum, swap the exe, restart.
6. **Usage without reading the login token:** test whether the VS Code extension runs Claude Code's `statusLine` and passes `rate_limits` (Open Island's approach: a managed `statusLine.command` caching `rate_limits`; it must never overwrite a user's own status line). If it works it could replace the OAuth endpoint, which would also help with SignPath's data-handling rules.
7. Older wishes: click-to-focus (`VSCODE_PID` / parent-process walk, pure window-choice function in Core), island position and card timings in the panel, README banner (original art only), re-measure latency (findings L8), and `smoke-overlay` / `smoke-usage` runs (ask first).
8. The GitHub "About" text still says "Clause Islet for Windows"; the user edits it with the gear on the repo page.

## Decisions and preferences
- **Least setup is the goal:** download, double-click, press Connect once. No installer, no copied helper exe, no admin rights.
- Commit by feature in small commits; **commit and push only when the user asks** (they asked to push after the rename; they have not asked since).
- End commit messages with the `Co-Authored-By` trailer for the model in use (the system prompt says which).
- **Never close the user's running island without asking**; use its quit event (`Local\AgentIslet-Quit`; `Local\ClaudeCodeIsland-Quit` for a build from before the rename) or the tray. Never leave processes of your own running (check `Get-CimInstance Win32_Process` for `*scratchpad*` paths).
- Never edit the real `~/.claude/settings.json` except through the app's Connect or the installer, and tell the user. Tests use `CLAUDE_CONFIG_DIR` / `--settings <temp file>` only.
- **Do not press keys or click on the user's screen** (no `SendKeys`, no real mouse) without asking: they interrupted such a run. Prefer UI Automation `InvokePattern`, offscreen rendering and posting to the local endpoint.
- All new UI matches the island: black, rounded, orange accent (`#E58C36`), Segoe UI. Milestones fire every 5 %, once per window. Usage polling needs explicit consent; the token is read-only, never printed, persisted or refreshed.
- Licence GPL-3.0 (same as Open Island, nothing copied). Signing: SignPath Foundation (free for open source), applied for by the user.

## Architecture
```
src/Island.Core        agent-neutral: IAgentIntegration + AgentEvents + PermissionVerdict, sessions, hub, transports
                       (IslandServer pipe, HookHttpServer web endpoint), ConfigStore, projects, usage engine, overlay logic
                       (OverlayState, IslandCollapse, UsageReveal, CardPresenter). No UI.
src/Island.ClaudeCode  ClaudeCodeAgent (payload -> events, FrameFromHttp, verdict -> hook output), HookPayload, ToolSummary,
                       Setup/ (HookSettings, SettingsFile, ClaudeCodeSetup), Usage/ (OAuth source), ClaudePaths, ProjectFolder
src/Island.Hook        island-hook.exe, the older forwarder: hook | simulate | install | uninstall (still works)
src/Island.App         WPF: App.xaml.cs (single instance, endpoint, pipes, tray, flags), PanelWindow + view-models (Sessions,
                       Projects, Usage, Claude Code, Debug), IslandWindow + OverlayController + OverlayViewModels, DemoDriver,
                       ShotsDriver, TrayIconHost, Theme.xaml
tests/                 Island.Core.Tests (TestAgent), Island.ClaudeCode.Tests + tests/fixtures/{hooks,usage} (sanitised payloads)
scripts/               publish-hook, bench-hook, install-local, make-icon, make-screenshots, smoke-app, smoke-app-ui, smoke-overlay, smoke-usage
docs/                  findings.md, design.md, distribution.md, HANDOFF.md, images/
```
Flow (web hooks): Claude Code `http` hook -> `HookHttpServer` (token + Host check) -> `IslandHub.HandleHttpAsync` -> `ClaudeCodeAgent.FrameFromHttp` -> `HandleAsync` (project gate) -> `Translate` -> `SessionStore` -> effects -> `OverlayController` / `PanelViewModel`. A permission request keeps its HTTP request open until `IslandHub.Answer`; the integration encodes the verdict and it becomes the response body. A closed connection drops the card. A null verdict means "no decision", so Claude Code shows its own prompt. The pipe path (forwarder) feeds the same hub.

## Build, test, run
```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')  # if dotnet is not found
dotnet build AgentIslet.sln        # fails with file-in-use errors while the user's island runs: use -o <scratch> for the App
dotnet test tests\Island.Core.Tests; dotnet test tests\Island.ClaudeCode.Tests   # run per project while the island runs
powershell -File scripts\make-screenshots.ps1 [-Exe <path>]    # remake docs/images from the app (offscreen)
powershell -File scripts\publish-hook.ps1                      # artifacts\hook\island-hook.exe (smoke scripts need it)
powershell -File scripts\smoke-app.ps1 | smoke-app-ui.ps1      # real app + real forwarder (they use the pipe)
powershell -File scripts\install-local.ps1                     # dev install via the helper route (closes a running island first)
dotnet run --project src/Island.App -- --demo                  # flags: --shots <dir>, --demo, --tray, --poll-usage, --allow-all-projects
```
Test-only environment: `ISLAND_DATA_DIR`, `ISLAND_INSTANCE_NAME` (own lock and quit event `Local\AgentIslet-<name>-Quit`), `ISLAND_PIPE_NAME` (one pipe, no legacy pipe), `CLAUDE_CONFIG_DIR` (the settings file the app reads and writes). Run scripts as `powershell -NoProfile -ExecutionPolicy Bypass -File ...` if blocked.

Headless Claude Code for experiments: the bundled binary at `~/.vscode/extensions/anthropic.claude-code-*/resources/native-binary/claude.exe`; `-p "<prompt>" --settings <throwaway.json> --setting-sources project,local --max-turns N` (leaves your real hooks out). Do **not** pass `CLAUDE_CONFIG_DIR` to it: that hides the login ("Not logged in").

## Gotchas learned the hard way
- Windows PowerShell 5.1 reads BOM-less `.ps1` as ANSI: write non-ASCII as `[char]0xB2`. `$pid` and `$args` are protected/automatic variables. `Start-Process -ArgumentList` joins with spaces: quote any argument that contains one.
- Patching scripts through nested shell quoting fails silently; use the Edit tool on the file. Bash `sed`/`perl` mangle `\C`-style backslash patterns.
- `Remove-Item` on very long paths fails (Claude Code makes long folder names under its config dir): use short fresh test folders.
- UI Automation: the island window and the panel share the title "Agent Islet" (and a second island has the same titles): select by **process id** and exact title. A WPF `MessageBox` shows its buttons as Panes; owned dialogs hang under the desktop's descendants (class `#32770`). The Sessions tab's label carries a count chip: match on the start of the name.
- Leftover test listeners or islands share ports and garble later runs: check for them before each run and stop only your own.
- `git reset --hard` discards uncommitted work. Save a patch first.
- WinForms analyzer WFAC010 is suppressed in the App csproj; the csproj removes the implicit `System.Windows.Forms` / `System.Drawing` usings, so import `System.IO` explicitly in the App.
- Hook payloads: `PreToolUse` fires before `PermissionRequest`; `Notification(permission_prompt)` arrives ~6 s later and duplicates it; a hook-issued deny fires neither `PostToolUse` nor `PermissionDenied`; `PermissionRequest` has no `tool_use_id` (match via `ToolSummary.CallKey`). `http` hooks cannot handle `SessionStart`.
- After the rename an old helper and a new app spoke different pipe names, which silenced the island (user report). That is why the app also listens on the old pipe name: never rename a wire identifier without keeping the old one alive.
- The usage endpoint's `resets_at` differs in fractional seconds on every call; milestone keys round to the minute.
- `git` warns about LF/CRLF (autocrlf=true). Harmless.

## Suggested first 10 minutes of the next session
1. `git fetch`, `git status -sb`, `git log --oneline -8`. Confirm with the user whether to push the 7 local commits.
2. Check the island is running and its hooks state (panel -> Claude Code tab), without changing anything.
3. `dotnet test` both test projects (expect 213 + 200).
4. Ask the user to press "Switch to the built-in link" and test a real approval, then continue with the list above.
