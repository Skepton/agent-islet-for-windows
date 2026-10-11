# Claude Code Island

Windows-only overlay ("island") that shows when Claude Code needs approval/input and when usage milestones are reached. Claude Code only. C# / .NET 8 / WPF.

**Read [PLAN.md](PLAN.md) first** — it is the source of truth (architecture, protocol, work packages, acceptance criteria). Real-world findings are in [docs/findings.md](docs/findings.md). Where PLAN.md says VERIFY, test it and write the result into docs/findings.md rather than guessing.

## Ground rules
- **Fail open.** Nothing in this project may block, slow, or change Claude Code's behaviour unless the user explicitly chose so on a card. Default hook answer = print nothing, exit 0.
- **Never modify `~/.claude/settings.json` in tests or during development** except through the installer code under test, pointed at a temp copy via `--settings <path>`. Back up before touching the real one. Do not leave hooks installed that you added.
- **Never read, print, log, or commit the contents of `~/.claude/.credentials.json`.** The usage client reads `accessToken` in memory only, never writes it anywhere (not logs, not state, not exceptions), and never refreshes it.
- Never commit anything from `logs/` (contains prompts/commands from real sessions). Fixtures in `tests/fixtures` are sanitized; keep them that way (no real usernames, prompts, or tokens).
- Don't log full `tool_input` / prompt text at default log level.

## Environment
- .NET SDK 8.0.425 pinned via `global.json`. If `dotnet` isn't found in a fresh shell, refresh PATH (`$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`).
- No Visual Studio / C++ build tools → **NativeAOT is unavailable**; the hook forwarder ships as self-contained single-file + ReadyToRun.
- Claude Code runs hook commands under **Git Bash** on Windows: use forward-slash absolute paths in double quotes.
- Bundled Claude binary for headless experiments: `~/.vscode/extensions/anthropic.claude-code-*/resources/native-binary/claude.exe` (`-p "<prompt>"`). Headless tests must use a throwaway `--settings` file / temp cwd, not the user's settings.
- Windows PowerShell 5.1 is the default shell; `Set-Content -Encoding utf8` writes a BOM — use `[IO.File]::WriteAllText` with `UTF8Encoding($false)` for JSON that other tools parse.

## Layout
```
src/Island.Core   shared models, IPC protocol, usage + milestone logic, settings merge
src/Island.Hook   forwarder exe (hook + install/uninstall/simulate subcommands)
src/Island.App    WPF overlay + tray
tests/            xUnit + tests/fixtures (real, sanitized payloads)
scripts/          publish-hook.ps1, bench-hook.ps1, smoke-app.ps1, smoke-app-ui.ps1, smoke-usage.ps1 (smoke scripts run after build + publish-hook; they need no island instance already running)
docs/             findings.md (verified behaviour), anything else long-lived
```

## Conventions
- C# 12, nullable enabled, warnings as errors, file-scoped namespaces. `System.Text.Json` only (source-generated contexts, no reflection serialization) so the forwarder stays trimmable.
- Every payload parser must tolerate unknown/missing fields (Claude Code versions differ). Parse against the fixtures in `tests/fixtures`.
- Keep UI logic in view-models that can be unit-tested without a window.
- Small, focused commits only when the user asks for them.
