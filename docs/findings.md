# Phase 0 findings (2026-10-11)

Environment: Claude Code **2.1.296** (VS Code extension `anthropic.claude-code-2.1.296-win32-x64`), Windows 11.

Phase 0 was run with throwaway Node logging hooks. **That tooling has been removed** (hooks and status line uninstalled from `~/.claude/settings.json`, logs deleted, `spike/` folder deleted). What survives is this document and the sanitised payloads in `tests/fixtures/`. References below to "the spike" and `control.json` describe how the evidence was gathered.

## Answered

| # | Question | Result |
|---|---|---|
| 1a | Do hooks run under the VS Code extension? | **Yes.** `CLAUDE_CODE_ENTRYPOINT=claude-vscode`. Hooks added to `~/.claude/settings.json` were picked up by the **already-running** session with no restart (it fired on the next tool call). |
| 1b | Which shell runs hook commands on Windows? | **Git Bash** (`SHELL=C:\Program Files\Git\bin\bash.exe`, `MSYSTEM=UCRT64`). Implication: use **forward-slash absolute paths in double quotes** (`"C:/Users/…/island-hook.exe" hook`). A path containing a space worked when quoted. Git for Windows is therefore a de-facto requirement for Claude Code on Windows. |
| 1c | Env available to hooks | `CLAUDE_PROJECT_DIR`, `CLAUDE_CODE_SESSION_ID`, `CLAUDE_PID` (the Claude Code process), `VSCODE_PID`, `CLAUDE_CODE_ENTRYPOINT`. `CLAUDE_PID` / `VSCODE_PID` are useful for the phase-6 "focus the right window" feature. |
| 2a | Event order for a tool needing permission | `PreToolUse` → `PermissionRequest` (~130 ms later) → *(tool runs)* → `PostToolUse` → `Stop`. So `PreToolUse` fires **before** the permission decision — it must NOT be treated as "the permission was resolved". Only `PostToolUse` (or a following `UserPromptSubmit`/`Stop`) means resolved. |
| 2b | Hook `allow` decision | Works: tool executed without any prompt (headless). |
| 2c | Hook `deny` decision | Works: tool not executed; Claude was told it was denied "by a spike hook" (our `message` reaches Claude). **`PermissionDenied` did not fire** for a hook-originated deny, and no `PostToolUse` either — the next event was `Stop`. So a card for a denied request is cleared by the *next event in that session* (`Stop`/`UserPromptSubmit`), but we already know the outcome in that case since we sent it. |
| 2d | `permission_suggestions` shape | Not only `addRules`. Observed for a Bash write outside the project: `[{"type":"addDirectories","directories":["C:\\…\\scratch-test"],"destination":"session"}]`. The "Always allow" button must pass suggestions through **opaquely** (echo them back in `updatedPermissions`) and label the button from `type` generically (e.g. "Allow + add directory", "Allow + always allow `<rule>`"). Do not assume `addRules`. |
| 3a | `PermissionRequest` coverage (partial) | Fired for **Bash** and **PowerShell** tool calls in the live VS Code session (13 requests logged). Edit/Write/WebFetch/MCP/plan-mode not yet seen (Q3). |
| 3b | `Notification` timing + payload | `notification_type: "permission_prompt"`, `message: "Claude needs your permission to use Bash"`, **no `title` field**. It fires **~6 s after** the matching `PermissionRequest` (observed +6.0 s, repeatedly). So **`PermissionRequest` is the immediate trigger for approval cards; `Notification` is a delayed duplicate** (de-duplicate: ignore a `permission_prompt` notification when a permission card for that session is already showing or was just resolved). `Notification` remains the only signal for idle/elicitation states. |
| 5 | Hook latency | Node logger: **~97 ms** per invocation wall-clock (Git Bash); process-spawn floor on this box ≈ **43 ms** (`cmd /c exit`). So a native exe forwarder should land ≈ 45–60 ms, node ≈ 100 ms per hook. With `PreToolUse`+`PostToolUse` that is 2 spawns per tool call: acceptable for node-class, native is better. Hooks fire sequentially-ish with Claude Code doing the waiting, so registering fewer events directly reduces overhead (see recommendation). |

## Added later the same day

| # | Question | Result |
|---|---|---|
| U1 | Usage endpoint | `GET https://api.anthropic.com/api/oauth/usage` with `Authorization: Bearer <accessToken from ~/.claude/.credentials.json>` + `anthropic-beta: oauth-2025-04-20` → **200 in ~370 ms** on a Pro account. Gives `five_hour`/`seven_day` `{utilization (0–100 float), resets_at (ISO-8601 µs +00:00)}`, per-model windows (null here), `extra_usage`, a normalised `limits[]` array (`kind: session\|weekly_all`, `percent`, `severity`, `is_active`), `spend`, `seven_day_breakdown`. ~15 further null keys with obfuscated names → ignore unknown keys. Fixture: `tests/fixtures/usage/oauth-usage.response.json`. Credentials file keys: `claudeAiOauth.{accessToken, refreshToken, expiresAt(ms), refreshTokenExpiresAt, scopes, subscriptionType, rateLimitTier}`; token had ~6.7 h life. **Token was never printed or stored.** |
| U2 | Status line | Still **zero invocations** in two observed VS Code sessions (one fresh session started but never sent a prompt → inconclusive). Decision: v1 doesn't depend on it (polling is primary). |
| Q2' | Dialog vs pending hook | A permission request in another concurrent session (cwd `soccer-fk\research`) ran our hook with a 20 s sleep. `Notification(permission_prompt)` fired at **+5.98 s**, i.e. while the hook was still running (heartbeats continued to +20 s) and at the *same* delay (+6.0 s) as when the hook returns instantly. ⇒ the dialog/notification timer is independent of the hook ⇒ **dialog is shown concurrently with a pending hook** (strong inference; not eyeballed). **Not yet tested:** whether answering the dialog kills the hook process. |
| M1 | Multi-session | The user really runs several sessions at once (3 non-headless session ids in ~25 min across 2 projects). Session ids differ per window; `cwd` distinguishes projects. |
| C1 | Side effects of testing | The 20 s test also delayed one permission prompt in that *other* session (control.json is global). Reset to 0 immediately. The spike logs contained commands/prompts from **all** local sessions; they have been deleted. |
| F1 | Fixtures | `tests/fixtures/hooks/*.json` generated from real logged payloads (sanitised: no real usernames, prompts or tool output). The generator was removed with the spike; edit the JSON by hand or capture new payloads through the real app. Two `permission_suggestions` shapes captured: `addRules` and `addDirectories`. `permission_mode` observed: `acceptEdits`, and Bash still triggers PermissionRequest under it. |
| T1 | Tooling | No Visual Studio / C++ build tools (no NativeAOT). .NET SDK 8.0.425 working. `winget` install succeeded unattended. |

## Still open — needs a human at a real prompt (to be tested with the real app in WP3, see "Open tests" below)

- **Q2 (narrowed, see Q2'):** does answering the permission dialog kill a still-running hook process? (Design is safe either way.)
- **Q3 (remaining):** do `idle_prompt` and `elicitation_dialog` (`AskUserQuestion`) fire in practice? Does `PermissionRequest` fire for Edit/Write, WebFetch, MCP tools, plan-mode exit? (`permission_prompt` and Bash/PowerShell coverage are confirmed above.)
- **Q4 (no longer blocking):** does the status line run in the VS Code extension? v1 uses endpoint polling instead (U1), so this only matters for an optional later status-line source.

## Design changes to make in PLAN.md based on the above
1. Don't use `PreToolUse` to clear permission cards (§4.5 card 1 / §2.2 table). Use `PostToolUse`, `UserPromptSubmit`, `Stop`, `SessionEnd`. `PreToolUse` can still mark the session "working" for non-permission cards only if no permission card is pending.
2. `permission_suggestions` handled opaquely (above).
3. Hook command string: Git Bash, forward slashes, quoted absolute path.
4. Consider dropping `PreToolUse` registration entirely (saves one process spawn per tool call). `PostToolUse` + `UserPromptSubmit` are enough to return a session to "working".
5. If Q4 fails, usage fallback options: (a) read `rate_limits` from somewhere else Claude Code writes locally (check `~/.claude` for cached usage — not yet investigated), (b) poll the same endpoint `/usage` uses (undocumented — avoid unless the user agrees), (c) restrict milestones to terminal CLI sessions and document the limitation.

## Open tests (run with the real app in WP3; the throwaway logging hooks used in Phase 0 are gone)

1. **Kill-on-answer (Q2):** with a `PermissionRequest` card showing, answer the dialog in VS Code instead. Does the forwarder process get killed, or does it keep waiting until its deadline? Either way the app clears the card (client disconnect, or `PostToolUse`).
2. **Coverage (Q3):** in a real session trigger an Edit, a `WebFetch`, plan-mode exit and an `AskUserQuestion`-style question, and idle ~60 s at the prompt. Record which of `PermissionRequest`, `Notification(idle_prompt)` and `Notification(elicitation_dialog)` arrive. The app's debug list is the place to see them.
3. **Status line (Q4, optional):** only matters if a status-line usage source is added later.

## Implementation measurements (WP0–WP2)

| # | Measurement | Result |
|---|---|---|
| M2 | Runtime startup of self-contained, single-file, ReadyToRun `island-hook.exe` (no args) | **median 33 ms** |
| M3 | Forwarder, garbage stdin (parse fails, no pipe) | median 43 ms |
| M4 | Forwarder, `Stop` payload, **app absent**, after fix below | **median 45 ms, min 43 ms** (20 runs). One 1.07 s outlier on the first run (cold start) |
| M5 | Same, **before** the fix | median 250 ms. Cause: `NamedPipeClientStream.ConnectAsync(timeout)` keeps retrying for the **full timeout** when the pipe does not exist. Every hook call cost 200 ms while the app was closed. |
| M6 | Fix | Check `\\.\pipe\` for the pipe name first (enumeration ≈ 3 ms, 530 entries on this machine) and throw `IOException` immediately if absent. Fail-open behaviour unchanged (exit 0, empty stdout). |
| M7 | Fail-open with app absent | exit 0, stdout empty, garbage stdin exit 0, silent |
| T2 | Unit + transport tests | 52 passing (fixtures, protocol, session state machine, hub, real named-pipe round trips, disconnect, deadline, missing app) |
| M8 | Forwarder latency **with the app running** (`simulate` of a `Stop` payload, 20 runs, app launched from the Debug build) | **median 50.3 ms**, min 48.0, max 64.6 (app absent: 45 ms). Includes pipe connect, frame write and the app's processing. |
| M9 | Real window, real forwarder | UI Automation clicks on the debug window returned the right decision to the forwarder for Allow, Deny (with message), Allow + remember (`addRules`), Allow + add folder (`addDirectories`) and Answer in terminal (no output). An unanswered request times out silently and the app keeps running; a second instance exits by itself. |
| M10 | Smart App Control | The unsigned Debug `ClaudeCodeIsland.exe` launched fine during these runs, although an earlier launch of an earlier build was blocked. Not predictable; signing decision still open. |
| M11 | Live usage poll through the app (`--poll-usage`, throwaway data folder) | Worked on first try: 5h 35% (announced 25), 7d 18% (announced 10), reset times in the future. No token or refresh token in any file the app wrote. |
| M12 | `resets_at` precision | The fractional seconds **change on every call** (`16:10:00.076076` earlier, `21:10:00.3070350` later), and the 5-hour window moved from 16:10 to 21:10 once activity started a new window. Confirms the milestone key must round to the minute and treat a new reset time as a new window. |
| M13 | WP5 tests | 124 tests total, 72 new: parsing real fixtures, every milestone rule, persistence, credentials (expired / missing / garbage / unchanged on read), the HTTP client with a fake handler (headers, 401/403/429/5xx, timeout, cancellation, no token in any log line), schedule maths, monitor loop and early wake-up. |
| M14 | **Focus safety of the island (PLAN §5.5), VS Code in front** | `scripts/smoke-overlay.ps1`, 16 checks pass: the island appears for a `PermissionRequest` without becoming the foreground window; it has `WS_EX_NOACTIVATE` (style `0x8080088`); `AutomationElement.FromPoint` at the centre of Yes / No returns that button; a **real mouse click** (`SetCursorPos` + `mouse_event`) on Yes / No makes the forwarder print the allow / deny decision; the foreground window is unchanged before, during and after. The island hides again after the answer. Not yet tried: Windows Terminal in front. |
| M15 | Overlay rendering | `--demo` screenshots (150 % display scale, one monitor): the island is top-centre, 480 DIP wide, slides in, and matches the reference's hierarchy (usage strip, orange dot, chips, attention line, tool label, command box, three equal buttons, pager, footer). Needs-input, finished (long reply scrolls), 35 % milestone (green) and sticky 90 % milestone (red) cards all render. `Segoe UI Variable Text` appears to be in use (not checked programmatically). |
