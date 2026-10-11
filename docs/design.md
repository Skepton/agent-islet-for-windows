# Overlay design (WP4)

Direction from the user: take inspiration from Open Island's look, **font sizes and layout**. This spec was written from the project's `demo.gif` (720 × 461, 341 frames, permission card in frames ~190-240, "done" card in ~145 and ~291). Nothing from that repo is copied. See PLAN.md "Design reference and licensing" for the open licence question.

**All numbers below are starting values measured or estimated from a compressed GIF (±15 %).** Build the demo mode, put it next to the reference frames, and tune. Sizes are WPF device-independent pixels, scaled from the GIF with the island ≈ 367 px wide in the GIF mapped to **480 DIP** (factor ≈ 1.3).

## Shape and position
- Black (`#000000`) panel attached to the **top edge of the screen**, horizontally centred on the monitor containing the foreground window. Flat top, large rounded bottom corners (**~20 DIP**).
- Windows has no notch, so there is nothing to grow out of: the panel just drops down from the top edge (≈ 200 ms ease-out, slide + fade) and retracts the same way.
- Width **480 DIP** (cap to 90 % of the work area on small screens). Height is automatic.
- Never takes focus; clicks work (see PLAN §4.5 and the focus prototype).

## Layout, top to bottom (permission state)
1. **Usage strip** (always the first row, also the entire collapsed state). Left: `Claude  5h 28%  1h 10m  |  7d 55%  2d`. The percentages are green (`#6FBF4A`-ish, going amber/red as they climb); the reset countdowns are grey. Right side: small icons for sound and settings. Text ≈ **11 px**.
2. **Session card**: rounded rectangle (radius **~13**), 1 px border barely lighter than black (`#232326`-ish), padding **~11**.
   - **Header row:** orange status dot (`#E58C36`, ~9 px) + title `project · what you asked` (**13 px, semibold, white**, one line, ellipsis). Right-aligned chips: `Claude Code`, terminal/editor name (e.g. `VS Code`), age (`<1m`): **10 px**, grey text on `#1E2128`-ish pill, radius full.
   - **Context line:** `You: <last prompt>` (**11 px**, grey `#9A9AA0`), one line.
   - **Attention line:** `Claude wants to run Bash.` (**11 px**, orange).
   - **Tool label:** warning triangle + `Bash` (**12 px**, semibold, orange).
   - **Command box:** rounded (radius ~10), background `#15120A` (very dark warm), padding ~10, height ~44. First line `$ <command>` in a **monospace 12 px bold, white** (`Cascadia Mono` / `Consolas`); second line the tool's own description in **10 px grey**. PowerShell commands show `PS>` instead of `$`; Edit/Write show the file path; WebFetch the URL.
   - **Button row:** three equal-width buttons, height **~28**, radius **~8**, gap **~6**, label **12 px semibold white**:
     - `No` → dark grey `#22272D`
     - `Yes` → orange `#E58C36`
     - `Always Allow (Bash)` → red `#BE3D30` (our "remember" suggestion; for an `addDirectories` suggestion the label is `Allow + add folder`).
3. **Footer:** `Show all N sessions` (**11 px**, grey, centred) when more than one session is active.

Our extra control, **Answer in terminal**, is a small text link under the buttons (not in the reference).

## Other states
- **Needs input / question:** same card. The attention line carries the question or notification text; if the question has options (AskUserQuestion), each option is a button in the same style (first = orange, others dark grey); free text answers fall back to "Answer in terminal". The reference demo does not show this state, so this is our extrapolation.
- **Finished (Stop):** card with a green `Done` tag at the right of the `You:` line and the reply below it (up to ~6 lines, scrollable, plain text first, markdown later). Auto-hides after a few seconds (setting).
- **Usage milestone:** a compact card (title `5h limit  ·  35 % used`, progress bar, `resets 21:10`) in the same style; the strip's percentage flashes once.
- **Collapsed:** only the usage strip (optional; can be hidden entirely).

## Colours (sampled where possible)
| Role | Value |
|---|---|
| Panel | `#000000` |
| Status dot / Yes button / warnings | `#E58C36` (sampled from the dot) |
| Always Allow button | `#BE3D30` (sampled) |
| No button | `#22272D` (sampled) |
| Command box | `#15120A` (sampled) |
| Secondary text | ~`#9A9AA0`, borders ~`#232326`, chips ~`#1E2128` (estimated) |
| Text | white |

## Fonts (macOS → Windows)
- UI: `Segoe UI Variable Text` (fallback `Segoe UI`) in place of SF Pro. Use `Segoe UI Variable Display` for the title if it reads better.
- Mono: `Cascadia Mono` (fallback `Consolas`) in place of SF Mono.
- Windows body text tends to look smaller than macOS at the same number, so expect to nudge sizes up by 0.5-1 px when comparing.

## Mapping to the data we have
| Element | Source | Gap |
|---|---|---|
| project name | `cwd` folder (done) | |
| `You: …` | `UserPromptSubmit.prompt` | Keep the **last prompt in memory only**, truncated; never persist. Not parsed yet. |
| `Claude wants to run Bash.` | tool name + permission request | have |
| command + description | `tool_input.command` / `.description` | `ToolSummary` returns one string today; add the description separately |
| `Always Allow (Bash)` | `permission_suggestions` | label by suggestion type; have the raw data |
| chip: agent | constant "Claude Code" | |
| chip: editor | `CLAUDE_CODE_ENTRYPOINT` / `VSCODE_PID` in the hook's environment | forwarder must send it in the frame (small protocol addition; also feeds the later "focus the right window" feature) |
| chip: age | session `LastSeen` / request time | have |
| usage strip | `UsageMonitor` latest snapshot (WP5, done) | reset countdown formatting |
| `Show all N sessions` | session store | have |

## What not to do
- Don't copy Open Island's code, artwork, icons or sounds into this repo until the licence question is settled, and don't use its name or logo.
- Don't pixel-match; match proportions and hierarchy, then tune for Windows type rendering.
