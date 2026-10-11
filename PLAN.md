# Claude Code Island — Implementation Plan (v2)

A Windows-only, Claude Code-only take on [Open Island](https://github.com/Octane0411/open-vibe-island): a small "island" that drops down from the top-centre of the screen when Claude Code needs you (permission approval, waiting for input) and when usage milestones are reached (e.g. 25% of the 5-hour limit used).

**v2 changes vs v1:** Phase 0 is done (results folded in). Usage data now comes from **polling the usage endpoint** (verified live); the status line is demoted to an optional extra and is **not installed in v1**. `PreToolUse` is no longer used. Work is split into agent-sized work packages with dependencies (§6). See `docs/findings.md` for raw evidence and `CLAUDE.md` for repo rules.

Legend: **[V]** verified on this machine · **[D]** from official docs, not observed · **[?]** unverified, test it and record the result in `docs/findings.md`.

---

## 1. Scope

**v1**
1. Island pops when a Claude Code session needs a human: permission request, elicitation/idle notification, (optionally) turn finished.
2. Approve / deny permission requests from the island (+ "allow and remember" using Claude Code's own suggestions).
3. Usage milestone popups for the 5-hour and 7-day windows.
4. Several concurrent sessions across projects/windows **[V: user runs ≥2 at once]**.
5. Tray icon, settings, one-click hook install/uninstall, optional start-with-Windows.

**Out of scope for v1:** other agents, macOS/Linux, jumping focus back to the originating window (phase 6), auto-update, installing a status line.

"Daily" limit in the original ask = Claude's **5-hour** window; there is no daily window. Weekly = **7-day**.

**Product decisions assumed (user did not object; change freely):**
- Stop ("finished") card: **on**, auto-hides after 6 s, toggleable.
- Milestones: **every 5 %** (5, 10, ... 100) for both windows, changed from 10/25/50/75/90 at the user's request. A jump over several steps still shows one popup, for the highest reached. Thresholds stay configurable.
- Usage polling uses the user's OAuth token → **explicit consent prompt on first run**, toggleable later.
- v1 does **not** touch `statusLine` in settings.

---

## 2. What we know about Claude Code (v2.1.296, VS Code extension, Windows 11)

### 2.1 Hooks
- **[V]** Hooks in `~/.claude/settings.json` run under the **VS Code extension** and are picked up by an **already-running session without restart** (took effect on the next tool call). `CLAUDE_CODE_ENTRYPOINT=claude-vscode`.
- **[V]** Commands run under **Git Bash** (`SHELL=C:\Program Files\Git\bin\bash.exe`, `MSYSTEM=UCRT64`). Use `"C:/abs/path/with spaces/island-hook.exe" hook` — forward slashes, double-quoted; a path with a space was tested OK.
- **[V]** Env available to hooks: `CLAUDE_PROJECT_DIR`, `CLAUDE_CODE_SESSION_ID`, `CLAUDE_PID`, `VSCODE_PID`, `CLAUDE_CODE_ENTRYPOINT`.
- **[V]** Hook stdout is only parsed for decisions; node-class hooks cost ≈97 ms wall-clock, process-spawn floor here ≈43 ms.
- **[D]** Settings shape: `{"hooks":{"<Event>":[{"matcher":"…","hooks":[{"type":"command","command":"…","timeout":<s>}]}]}}`. Default command timeout 600 s (30 s for `UserPromptSubmit`; `SessionEnd` shares a 1.5 s budget). Matcher `""`/`*`/omitted = all. Fields `async`, `asyncRewake`, `shell`, `onFailure` exist **[?]** — `async: true` might let non-blocking hooks not delay Claude at all; test it in WP2.
- **[D]** `type: "http"` hooks exist; we use `command` for fail-open control and compatibility.

### 2.2 Events (payload fixtures with real shapes: `tests/fixtures/hooks/`)

| Event | Use | Notes |
|---|---|---|
| `SessionStart` | register session | `source`, `model`, `session_title`. **[V]** |
| `UserPromptSubmit` | session → working; dismiss non-permission cards | `prompt` (never persist). **[V]** |
| `PermissionRequest` | **blocking** approve/deny card | `tool_name`, `tool_input`, `permission_suggestions[]`, `permission_mode`. **[V]** fires for **Bash and PowerShell**; Edit/Write/WebFetch/MCP/plan-exit **[?]** |
| `PostToolUse` | permission was resolved (answered anywhere) → clear card; session → working | **[V]** fires after an approved tool runs. **Not** fired when a hook-issued *deny* blocks the tool. |
| `Notification` | needs-input states | `notification_type`, `message`; **no `title`** observed. **[V]** `permission_prompt` ("Claude needs your permission to use Bash"). `idle_prompt`, `elicitation_dialog`, `elicitation_url_dialog`, `agent_needs_input` **[?]** |
| `Stop` | turn finished | `last_assistant_message`, `stop_hook_active`. **[V]** |
| `SessionEnd` | drop session | tiny time budget. **[V]** |
| ~~`PreToolUse`~~ | **not used** | **[V]** fires *before* `PermissionRequest` (~130 ms earlier) so it can't signal "resolved"; skipping it also saves one process spawn per tool call. |
| `PermissionDenied` | not used | **[V]** did *not* fire for a hook-issued deny; **[?]** for a user deny in the UI — if it fires, treat as "resolved". |

Immediate vs delayed: **[V]** `Notification(permission_prompt)` arrives **≈6.0 s after** `PermissionRequest`, *regardless of how long the PermissionRequest hook runs* (identical +5.98 s with a 20 s sleeping hook). So: **`PermissionRequest` is the trigger for approval cards; the `permission_prompt` Notification is a duplicate and must be de-duplicated** (ignore if a permission card for that session is showing or was resolved in the last ~10 s). Notification stays the only signal for idle/elicitation.

### 2.3 PermissionRequest decisions
Hook prints JSON to stdout, exits 0:
```json
{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","updatedInput":{},"updatedPermissions":[]}}}
{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"…","interrupt":false}}}
```
- **[V]** `allow` → tool ran with no prompt. **[V]** `deny` → tool did not run, Claude was told our `message`.
- **[V]** No output/exit 0 → normal permission flow (dialog). That is our fail-safe.
- **[D]** exit code 2 is not honoured for this event; `updatedInput`/`updatedPermissions` allow-only; `message`/`interrupt` deny-only.
- **[V]** `permission_suggestions` has **at least two shapes**: `{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"node -e ' *"}],"behavior":"allow","destination":"localSettings"}` and `{"type":"addDirectories","directories":["…"],"destination":"session"}`. The island must treat suggestions **opaquely**: echo the chosen suggestion(s) back in `updatedPermissions`, and label the button from `type` generically ("Allow + remember rule `node -e ' *`", "Allow + add directory `…`"). Never assume `addRules`.
- **Concurrency with the terminal dialog [V-indirect]:** the 6 s notification timer starts at `PermissionRequest` start even while our hook is still running ⇒ **the VS Code dialog is almost certainly shown *while* the hook is pending** (our card is a *parallel* answer path). **[?] still to test directly:** does answering in VS Code kill a still-running hook process? Design must be safe either way (forwarder tolerates being killed; island clears the card when it sees `PostToolUse`/`Stop`/`UserPromptSubmit`/`SessionEnd` for that session or the pipe client disconnects).

### 2.4 Usage / rate limits
**Source A — usage endpoint (primary). [V]** `GET https://api.anthropic.com/api/oauth/usage`, headers `Authorization: Bearer <accessToken>` and `anthropic-beta: oauth-2025-04-20`. Undocumented (reverse-engineered by ccusage & friends; same call Claude Code's `/status` makes). Verified 200 in ~370 ms on a Pro account. Response (fixture: `tests/fixtures/usage/oauth-usage.response.json`):
```json
{"five_hour":{"utilization":28.0,"resets_at":"2026-10-11T16:10:00.076076+00:00", …},
 "seven_day":{"utilization":4.0,"resets_at":"2026-10-14T13:00:00.076101+00:00", …},
 "seven_day_opus":null,"seven_day_sonnet":null,"extra_usage":{…},
 "limits":[{"kind":"session","percent":28,"severity":"normal","resets_at":"…","is_active":true},
           {"kind":"weekly_all","percent":4,"severity":"normal","resets_at":"…","is_active":false}],
 "spend":{…}, "seven_day_breakdown":{…}, …~15 other null keys with odd names}
```
- `utilization` is 0–100 (float). `resets_at` is **ISO-8601 with microseconds and offset**. Many keys are `null`; unknown keys must be ignored; **any window may be `null`** (e.g. no active window) → treat as "no data", don't alert.
- Credentials: `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth.{accessToken, refreshToken, expiresAt (ms epoch), subscriptionType ("pro"), rateLimitTier}`. Plaintext file on Windows. Access token lifetime was ≈6.7 h at check time. Honour `CLAUDE_CONFIG_DIR` if set **[?]**.
- **Token rules (hard):** read `accessToken` in memory only; **never refresh** (refresh-token rotation could sign Claude Code out); never write/log/echo the token (including in exceptions and HTTP debug logs); if `expiresAt` is past or response is 401/403 → skip this poll, retry later (Claude Code refreshes it when used); if file missing / no `claudeAiOauth` (API-key users) → feature silently disabled with a tray hint.
- Polling: every **2 min** while any session had an event in the last 10 min; every **15 min** otherwise (setting; 0 = off). Exponential back-off (max 30 min) on errors/429, honour `Retry-After`. Cache last good value. Jitter ±10 %.
- The endpoint is unofficial → wrap in `IUsageSource`, treat schema drift (missing `utilization`, type change) as "unavailable" not a crash, and surface a tray warning after N consecutive failures.

**Source B — status line JSON (optional, off in v1). [D]** `rate_limits.{five_hour,seven_day}.{used_percentage,resets_at(epoch s)}` (Pro/Max only, after first response). **[V]** has *not* fired in the VS Code extension in two sessions (inconclusive: the fresh session never sent a prompt). Keep the `IUsageSource` abstraction so a future `island-hook statusline` can feed the same engine (fixture: `tests/fixtures/usage/statusline.rate_limits.json`).

### 2.5 Reference architecture (Open Island)
Native Swift/AppKit; App (UI) + Core (models, Unix-socket bridge, persistence) + Hooks CLI (reads stdin, forwards, fails open) + Setup CLI (writes hook entries). Helper binary lives at a stable per-user path so repo moves don't break hooks. We mirror this.

---

## 3. Environment (this machine)
- Windows 11 10.0.26200 x64. .NET SDK **8.0.425** installed + pinned (`global.json`); fresh shells may need PATH refresh.
- **No Visual Studio / C++ build tools** → no NativeAOT (could install VS Build Tools "Desktop development with C++", ~3 GB, if forwarder latency proves too high).
- Node 24 present (not used by the product), Git present, repo `git init`-ed on `main`, **no commits yet**.
- VS Code Claude extension 2.1.296 with bundled `claude.exe` (headless `-p` usable for tests). `claude` is not on PATH.
- User has **≥2 concurrent Claude sessions** (e.g. this project and `soccer-fk\research`).
- **The Phase 0 spike tooling has been removed.** Its hooks and status line were uninstalled from the user's `~/.claude/settings.json` (now: `model`, `modelSettings`, `autoUpdatesChannel`, no hooks, no status line), its logs and the `spike/` folder were deleted. A pre-spike backup remains at `~/.claude/settings.json.island-backup-20261011141006` (outdated; safe to delete once the user is happy with their settings).

---

## 4. Architecture

```
Claude Code ──hook stdin──▶ island-hook.exe ──named pipe──▶ ClaudeCodeIsland.exe (WPF overlay + tray)
                       ◀──stdout decision (PermissionRequest only)◀──┘
                                                         ▲
IUsageSource: EndpointPoller (HTTPS poll) ───────────────┘ → MilestoneEngine → cards
```

**Stack:** C# / .NET 8, WPF overlay (transparent topmost windows are simple in WPF; awkward in WinUI 3), `System.Windows.Forms.NotifyIcon` for the tray (no third-party deps).

### 4.1 Layout
```
ClaudeCodeIsland.sln
src/Island.Core   models (STJ source-gen), IPC protocol, PermissionDecision builder, SessionState machine,
                  IUsageSource + endpoint client + MilestoneEngine, SettingsMerger, paths/config
src/Island.Hook   exe "island-hook": `hook` (forwarder), `install`, `uninstall`, `simulate <fixture>`
src/Island.App    exe "ClaudeCodeIsland": pipe server, session store, overlay, tray, settings UI
tests/Island.Core.Tests   xUnit against tests/fixtures
tests/fixtures/{hooks,usage}   real, sanitized payloads (already present)
```

### 4.2 IPC
- Named pipe `\\.\pipe\claude-code-island-<current user SID>`, `PipeOptions.CurrentUserOnly` on **both** ends (the pipe can approve tool calls → security boundary).
- One connection per hook call, UTF-8 newline-delimited JSON.
  - C→S: `{"v":1,"id":"<guid>","kind":"hook","receivedAt":<unix ms>,"clientPid":<n>,"payload":<raw stdin JSON>}`
  - S→C (PermissionRequest only): `{"v":1,"id":"<same>","decision":<§2.3 decision object>|null}`; `null` = no decision.
- Ignore unknown fields and unknown `kind`; `v` lets old forwarders and new apps coexist.
- The server must detect a **dropped client** (forwarder killed because the user answered in the terminal) and clear that card.

### 4.3 `island-hook.exe hook` — fast, silent, fail-open
- Read stdin fully; connect to pipe with ~200 ms timeout. Any problem (no app, pipe error, exception) → **exit 0, print nothing, write nothing to stderr**.
- Non-blocking events: send, exit immediately (≤300 ms total budget; `SessionEnd` ≤150 ms).
- `PermissionRequest`: send, wait for response up to an internal deadline (default **540 s**, < 600 s hook timeout; setting). Decision → print JSON, exit 0. `null`/closed/deadline → print nothing, exit 0.
- Startup latency matters (spawned per `PostToolUse`). Build: self-contained single-file, ReadyToRun, trimming, `InvariantGlobalization`, no reflection JSON; forward `payload` as raw text/`JsonElement`, parse only `hook_event_name` & `session_id` in the forwarder. **Acceptance:** median wall-clock for a non-blocking event with the app running **< 80 ms** (node baseline here is ~97 ms; floor ~43 ms) and < 150 ms with the app absent; measure and record in `docs/findings.md`. If missed, options: install VS Build Tools and publish NativeAOT, or drop `PostToolUse` registration (see §4.7).
- Try `"async": true` on non-blocking hook entries **[?]**; adopt only if it demonstrably doesn't change behaviour and reduces Claude-side wait.

### 4.4 Session model (Island.Core)
Keyed by `session_id`; display name = leaf folder of `cwd` (fallback `session_title`); remember `permission_mode`, `CLAUDE_PID` isn't in payload (forwarder may add its parent-process info later for phase 6).
States and transitions:

| From → event | To |
|---|---|
| any → `SessionStart` | Idle |
| any → `UserPromptSubmit` | Working (clear needs-input/finished cards for session; **not** a pending permission card — that is cleared by the same signals + the rules below) |
| any → `PermissionRequest` | NeedsPermission (card + held pipe connection) |
| NeedsPermission → `PostToolUse` / `UserPromptSubmit` / `Stop` / `SessionEnd` / client disconnect | resolved elsewhere → reply `null`, close card |
| Working → `Notification` of needs-you type | NeedsInput |
| any → `Stop` (and `stop_hook_active` false) | Finished |
| any → `SessionEnd` | removed |
Sessions with no event for 12 h are pruned. All state in memory; **persist nothing from payloads** (no prompts, commands, file contents) to disk.

### 4.5 `Island.App` (WPF)
- Single instance (`Local\ClaudeCodeIsland` mutex). Async pipe server, many concurrent connections (a permission request holds one open for minutes). Tray menu: Sessions, Pause notifications, Settings, Install/Repair hooks, Uninstall hooks, Start with Windows, Quit.
- **Overlay window:** `WindowStyle=None`, `AllowsTransparency`, transparent bg, `Topmost`, `ShowInTaskbar=False`, `ShowActivated=False`; `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`; toggle `WS_EX_TRANSPARENT` (click-through) when nothing is shown. **[?]** buttons stay clickable under `NOACTIVATE`, and the terminal/VS Code keeps keyboard focus when the card appears (this is the core UX risk — prototype it first, WP4).
- Per-monitor DPI v2 manifest; top-centre of the monitor of the foreground window (fallback primary), inside the work area; re-position on display changes. Drop-down animation ~200 ms ease-out. Suppress non-critical cards (finished, milestones) when `SHQueryUserNotificationState` says fullscreen/presentation; queue them. Permission cards still show (setting).
- One card at a time; priority NeedsPermission > NeedsInput > milestone > Finished; "1 of N" with next/prev.
- **Cards**
  1. *Permission:* project, tool, human summary of `tool_input` (Bash `command`, PowerShell `command`, Edit/Write `file_path`, WebFetch `url`, else compact JSON; truncated, expandable), `permission_mode`. Buttons **Allow**, **Allow + remember** (one button per distinct suggestion `type`, shown only if present; tooltip = exact rule/dirs), **Deny** (+optional reason → `message`), **Answer in terminal** (reply `null`). Stays until resolved. Optional sound (off by default).
  2. *Needs input:* project + `message`; hides on `UserPromptSubmit`/click.
  3. *Finished:* project + first ~120 chars of `last_assistant_message`; auto-hide 6 s; toggle off.
  4. *Milestone:* "5-hour limit · 50 % used · resets 16:10" + bar; auto-hide 8 s, ≥90 % until clicked.
- Dedupe: ignore a `permission_prompt` Notification while a permission card is open / resolved <10 s ago.

### 4.6 Usage + milestones (Island.Core, heavily unit-tested)
- Normalised model: `UsageWindow { Id (FiveHour|SevenDay|…), Percent (0–100 double), ResetsAt (DateTimeOffset) }`. Source-specific parsing (ISO string vs epoch seconds) happens in the source adapters.
- Window key = `(Id, ResetsAt rounded to the nearest minute)` — the endpoint reports fractional seconds (`16:10:00.076`) and the status line reports epoch seconds; rounding makes both sources agree.
- On each reading: highest threshold `t ≤ percent` not yet notified for this key → notify **only that one** (no burst of five), record all lower ones as notified. New key (window rolled over) ⇒ nothing notified. Reading lower than last seen for the same key ⇒ ignore (out-of-order). `null`/missing window ⇒ ignore.
- First reading after install: show only the highest already-crossed threshold, once.
- Persist `{key → highest notified threshold}` + last reading to `%LOCALAPPDATA%\ClaudeCodeIsland\state.json` (atomic temp+replace); prune keys whose reset is in the past.
- Thresholds/enabled windows configurable; `spend_limit`/extra usage out of scope for v1.

### 4.7 Install / uninstall (`island-hook install|uninstall [--settings <path>] [--dry-run]`)
- Copy `island-hook.exe` (+ app) to `%LOCALAPPDATA%\ClaudeCodeIsland\bin\` so hooks survive rebuilds/moves.
- Edit `~/.claude/settings.json` (or `--settings`): back up to `settings.json.island-backup-<ts>`; parse with `JsonNode`, preserve all unknown keys/ordering as far as possible; invalid JSON → abort, change nothing; show diff in `--dry-run`/UI; write atomically **without BOM**.
- Register one matcher group per event: `SessionStart`, `UserPromptSubmit`, `PermissionRequest` (timeout 600), `PostToolUse`, `Notification`, `Stop`, `SessionEnd` (timeout 2). Command: `"C:/…/island-hook.exe" hook`. **Not** `PreToolUse`. Our entries are identified by the exe path/name; install is idempotent; other users' hooks are never touched; uninstall removes only ours and leaves the file otherwise byte-equivalent in content.
- **No status line is installed in v1.** (Optional later: `install --statusline` wrapping any existing command, never overwriting silently.)
- If `PostToolUse` latency turns out to hurt, the fallback is to register it only until WP2's measurements say otherwise; without it, permission cards cleared elsewhere would linger until `Stop`/`UserPromptSubmit`/disconnect — acceptable degradation.
- Start with Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- Compatibility: only `command` hooks, tolerant parsers, versioned IPC, self-contained publish (Windows 10 1607+/11, x64; arm64 optional).

### 4.8 Config & logs
`%LOCALAPPDATA%\ClaudeCodeIsland\config.json` (thresholds, card toggles, durations, poll intervals, usage-polling consent, permission deadline, fullscreen behaviour, sound), `state.json` (milestones), `logs\` (rolling, ~5 MB cap, no payload contents / no tokens at default level).

---

## 5. Remaining unknowns (do inside the work packages; record in `docs/findings.md`)
1. **Kill-on-answer:** does answering the permission dialog in VS Code kill a still-running `PermissionRequest` hook? (WP2/WP3 acceptance test with a real session; design is safe either way.)
2. **PermissionRequest coverage:** Edit/Write, WebFetch, MCP tools, plan-mode exit, `AskUserQuestion`; and whether `idle_prompt` / `elicitation_dialog` really fire (WP3 manual test with `simulate` + real session).
3. Does the status line ever run in the VS Code extension? (Optional; only matters if we ever add Source B.)
4. `async: true` semantics; forwarder latency vs the 80 ms target; `CLAUDE_CONFIG_DIR` handling.
5. Overlay focus behaviour (`NOACTIVATE`, clickable buttons, no focus steal).
6. Does `PermissionDenied` fire for a user deny in the UI?
7. **Smart App Control is enforced on the dev machine** (`VerifiedAndReputablePolicyState = 1`). All our binaries are unsigned, so they can be blocked, including `island-hook.exe` (which every hook call runs) and the app. Observed enforced blocks (Event 3077/3118 in `Microsoft-Windows-CodeIntegrity/Operational`): `Island.Core.Tests.dll` loaded by `testhost.exe` (15:28, later runs loaded fine) and `ClaudeCodeIsland.exe` (Debug build, 15:40, when launched with `dotnet run`). Unsigned `island-hook.exe` was run 40+ times without a block, but that is cloud-reputation luck, not something to rely on. SAC checks every loaded binary, including managed DLLs, and Microsoft documents no per-app exemption. **Decide before WP6 installs hooks:** (a) sign shipped binaries with a publicly trusted certificate (self-signed does not satisfy SAC; Microsoft's Artifact Signing, formerly Trusted Signing, is one route, eligibility and price unverified), or (b) turn SAC off in Windows Security (Microsoft says recent Windows updates allow turning it back on without a reinstall; verify on this build). For release builds publish single-file so there is one exe per program to sign. Do not add workarounds that bypass SAC. Diagnose blocks with the CodeIntegrity log, events 3076/3077.

---

## 6. Work packages (for parallel agents)

Dependencies: **WP0 → WP1 → {WP2, WP3, WP5, WP6}; WP4 can start after WP1 using a fake session store; WP7 last.** Each WP lists its acceptance criteria; none may modify the real `~/.claude/settings.json` except WP6's installer under `--settings` temp copies (and a final, user-approved real install).

### WP0 — Scaffold
`.sln`, three projects (net8.0 / net8.0-windows for App with `UseWPF` + `UseWindowsForms`), xUnit test project, `Directory.Build.props` (nullable, warnings-as-errors, C# 12), `publish` profiles (self-contained win-x64; Hook = single-file + R2R + trimmed). **Done when** `dotnet build` and `dotnet test` pass from a fresh shell and `dotnet publish` of Hook produces one exe.

### WP1 — Core contracts
Models + STJ source-gen contexts for all hook payloads (tolerant: unknown fields ignored, all optional except `hook_event_name`), IPC envelope/response, `PermissionDecision` builder (allow/deny/remember using opaque suggestions), session state machine (§4.4), `HookSummary` helper (tool-input → one-line summary). Tests parse **every file in `tests/fixtures/hooks`**, round-trip the protocol, and cover each state transition incl. dedupe of `permission_prompt`. **Done when** tests green; no UI/OS dependencies in Core.

### WP2 — Hook forwarder
`island-hook hook` per §4.3 + `simulate <fixture> [--wait]` that sends a fixture through the pipe. Tests: app absent → exit 0, empty stdout, <150 ms; app present (test pipe server) → envelope received; PermissionRequest allow/deny/null/deadline/disconnect paths; garbage stdin → exit 0. Benchmark script + results into findings (target <80 ms). Evaluate `async`. **Done when** acceptance numbers recorded and all paths tested.

### WP3 — App shell + pipe server + tray
Single-instance app, pipe server wired to `SessionStore` (WP1), held connections for PermissionRequest with client-disconnect detection, tray menu, debug window listing sessions/events (permission requests answerable via buttons — no overlay yet). **Done when** `island-hook simulate` fixtures drive the store; real Claude Code events appear (manual test with the user); killing the app leaves Claude Code unaffected.

### WP4 — Overlay UI (can use a fake store)
Overlay window + animations + all four cards + multi-item queue + positioning/DPI + fullscreen suppression, view-model unit tests. **First deliverable is a small prototype that answers §5.5** (card appears without stealing focus from VS Code/terminal; buttons clickable). **Done when** a demo mode (`--demo`) cycles all card types and the focus test passes on this machine in VS Code and Windows Terminal.

### WP5 — Usage + milestones
`IUsageSource`, `OAuthUsageSource` (credentials reader honouring §2.4 token rules, HTTP client, parser against the fixture incl. null windows/unknown keys/bad JSON, back-off), `MilestoneEngine` + persistence (§4.6), polling scheduler (active/idle intervals). Unit tests for thresholds, rollover, out-of-order, first-run, rounding between sources, expired token, 401/429, schema drift; **a test that asserts no code path logs or serialises the token** (e.g. HTTP handler wrapper + log capture). Manual smoke against the real endpoint is allowed once, never printing the token. **Done when** tests green and a live poll produces the same percentages as the fixture shape.

### WP6 — Installer
`install`/`uninstall`/`--dry-run`/`--settings` per §4.7 with a test matrix on temp copies: empty file, only-model file (the user's current shape), existing unrelated hooks on the same events, existing island entries (idempotent re-install), invalid JSON, BOM present, CRLF. **Done when** install→uninstall leaves content identical (JSON-equal) in every case and never removes foreign hooks.

### WP7 — Packaging, settings UI, docs
Settings window (+ usage-consent dialog), start-with-Windows, publish script producing a zip, README (install, privacy: what the island reads/sends — pipe is local, usage poll goes only to api.anthropic.com), uninstall instructions. **Done when** a clean-profile install → use → uninstall round trip is verified and `settings.json` is back to original.

### Later (phase 6+)
Focus the originating window (parent-process walk from forwarder → window handle), keyboard shortcuts, theming, optional status-line source, MSIX/winget, signing, NativeAOT forwarder.

---

## 7. Safety rules (every WP)
- Fail open everywhere; default hook answer = nothing.
- Never touch the user's real settings in tests; never print/persist the OAuth token; never persist prompts/commands/tool output; pipe is current-user-only; logs contain no payload contents.
- Tolerate missing/extra fields from any Claude Code version; version the IPC.
- Don't commit `logs/`; keep fixtures sanitized.

---

## 8. Implementation status

| WP | Status | Notes |
|---|---|---|
| WP0 Scaffold | done | `ClaudeCodeIsland.sln`, Core / Hook / App / tests, `Directory.Build.props` (warnings as errors). Builds with 0 warnings. |
| WP1 Core contracts | done | Tolerant payload parsing, frames, decisions, suggestion labels, tool summaries, session state machine. Tests parse every captured fixture. |
| WP2 Hook forwarder | done | `island-hook hook` and `simulate <file> [secs]`. Fail-open verified. Latency 45 ms median with the app absent (target < 80 ms). Pipe-exists check avoids a 200 ms connect stall. |
| WP3 App shell + pipe server + tray | done (one manual check left) | App hosts `IslandServer`/`IslandHub`, single-instance mutex, tray icon (Show / Quit), debug window (pending permission cards with Allow / remember / Deny / Answer in terminal buttons, sessions, event log), `--tray` starts hidden, 10-minute prune. `scripts/smoke-app.ps1` (10 checks) and `scripts/smoke-app-ui.ps1` (clicks the real buttons via UI Automation) pass. Latency with the app running: 50 ms median. **Left:** a real Claude Code session driving it (needs WP6's installer), and eyeballing the tray icon. |
| WP4 Overlay UI | in progress | Done and committed: card queue and priorities (`OverlayState`), usage formatting, last prompt / editor name / command description plumbing, theme, per-monitor DPI manifest, Win32 helpers. **Left:** card view-models, the overlay window, the controller, `--demo` mode, the focus-safety smoke test, visual tuning. Step-by-step in `docs/HANDOFF.md`. |
| WP5 Usage + milestones | done (UI not wired) | `UsageParser` (endpoint + status-line shapes), `OAuthCredentialsReader` (read-only; `SecretToken` redacts itself), `OAuthUsageSource` (never logs the token, no request when the token is expired), `MilestoneEngine` + `MilestoneStateStore` (atomic, `ISLAND_DATA_DIR` override), `PollSchedule` (2 min active / 15 min idle, backoff, Retry-After, jitter), `UsageMonitor` (wakes early on activity). 72 new tests (124 total). App polls only with the explicit `--poll-usage` flag (stands in for the first-run consent until WP7); the debug window shows usage and logs milestones. `scripts/smoke-usage.ps1` verified a live poll and that no token reaches any file. **Left:** the milestone popup itself (WP4) and the consent dialog + threshold settings (WP7). |
| WP6 Installer | not started | Must never touch the real settings in tests. |
| WP7 Packaging, settings UI, docs | not started | `scripts/publish-hook.ps1` exists for the forwarder. |

Scripts: `scripts/publish-hook.ps1` (single-file forwarder into `artifacts/hook`), `scripts/bench-hook.ps1` (per-call latency), `scripts/smoke-app.ps1` and `scripts/smoke-app-ui.ps1` (launch the real app and drive it with the real forwarder; the UI one briefly shows a window), `scripts/smoke-usage.ps1` (one live usage poll against a throwaway data folder). All smoke scripts need no island instance already running.

### Design reference and licensing (decision pending)
The full spec (layout, sizes, colours, fonts, data mapping) is in `docs/design.md`. The target look is Open Island's (black panel hanging from the top edge, orange status dot, `project · prompt` title, grey chips, dark command box, three equal buttons No / Yes / Always Allow, "Show all N sessions" footer, usage strip `5h 28% · 7d 55%` always visible). Open Island is **GPL v3** ("fork it, mod it, ship your own version"). Reusing its code, design files (`design/v6-bundle`, `design/v8-bundle`) or `Assets/Brand` means this project must also be GPL-3.0, and brand assets/name need the maintainers' say-so. Nothing from that repo has been copied into this one. Its Swift/AppKit code is not directly reusable in WPF; assets and design tokens might be. **Open:** pick this project's license, then check what is actually in those folders.
