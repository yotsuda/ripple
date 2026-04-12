# Changelog

All notable changes to splashshell are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows [Semantic Versioning](https://semver.org/).

## [0.4.0] - 2026-04-12

### Added
- **`peek_console` tool** — read-only snapshot of what a console is currently displaying. On Windows, reads the console screen buffer directly via `ReadConsoleOutputCharacterW` for an exact match with the visible terminal. On Linux/macOS, falls back to a built-in VT-medium terminal interpreter with fixed viewport, scrolling, alternate screen buffer, and save/restore cursor. Accepts a console selector (PID or display-name substring) or defaults to the active console.
- **`send_input` tool** — send raw keystrokes to a busy console's PTY input. Supports C-style escape sequences (`\r` for Enter, `\x03` for Ctrl+C, `\x1b[A` for arrow up, `\\` for literal backslash). Rejected when the console is idle (use `execute_command` instead). Console must be specified explicitly for safety. Max 256 chars per call.
- **`partialOutput` on execute timeout** — when `execute_command` times out, the response now includes a snapshot of what the console has been printing so far, so the AI can immediately diagnose stuck commands (watch mode, interactive prompts, stalled progress) without calling `wait_for_completion`.
- **Multi-line pwsh command support** — multi-line PowerShell commands (heredocs, foreach, try/catch, nested scriptblocks, comments) are handled via tempfile dot-sourcing. The command body is written to a temp `.ps1` file and dot-sourced, so session state (variables, functions) persists. A synthetic colorized echo replaces the dot-source line in the visible console, and PSReadLine history skips the internal tempfile path via `AddToHistoryHandler`.
- **Console selector for peek/send_input** — both tools accept a PID number or display-name substring (e.g. "Reggae" matches "#43060 Reggae"). Ambiguous matches are rejected. `peek_console` allows omitting the selector (defaults to active console); `send_input` requires it for safety.
- **Busy console workflow** — `execute_command` timeout responses now include a `partialOutput` snapshot for immediate diagnosis. From there the AI can `send_input` (respond or Ctrl+C), `wait_for_completion` (wait), or `peek_console` (get a fresher snapshot later).

### Changed
- **Recent-output ring buffer** — `CommandTracker` now maintains a 4 KB circular buffer fed from every PTY byte unconditionally (AI and user commands alike), with OSC C clearing to drop PSReadLine typing noise and claim-handshake clearing to drop prior-session residue.
- **VT-medium terminal interpreter** — the ring buffer snapshot is processed through a multi-row VT state machine handling CR/LF/BS/HT, CSI cursor positioning (CUU/CUD/CUF/CUB/CHA/CUP/HVP/VPA/CNL/CPL), EL/ED erasure, scroll regions (DECSTBM), alternate screen buffer (`\e[?1049h/l`), save/restore cursor (`\e7`/`\e8`, `\e[s`/`\e[u`), reverse index (`\eM`), SGR/OSC as no-ops, and DEC window manipulation (`\e[<params>t`) as a full-grid clear trigger. Fixed viewport with soft line wrap and vertical scrolling.
- **`_output` renamed to `_aiOutput`** to disambiguate from the new ring buffer. AI command result slicing via OSC C/D markers is unchanged.
- **Cache drain in `peek_console`** — every `peek_console` call now also drains cached outputs and detects closed consoles, matching `execute_command` and `wait_for_completion` behavior.

### Fixed
- **Dot-source line visible in console** — the `\e[<N>F\e[0J` erase sequence now dynamically calculates wrap row count based on terminal width, so the full dot-source input (which can exceed 200 chars and wrap to 2-3 rows) is erased completely.
- **Multi-line command cursor position** — the colorized echo is now emitted from inside the tempfile via `[Console]::OpenStandardOutput()`, bypassing pwsh's host TextWriter layer that was rewriting cursor-control escapes into absolute positioning. This keeps the child's virtual buffer cursor in sync with the visible terminal.
- **PSReadLine history pollution** — `.splash-exec-*.ps1` dot-source lines are excluded from PSReadLine history via `AddToHistoryHandler` in `integration.ps1`.

### Known limitations
- `peek_console` on Linux/macOS uses the VT-medium interpreter which may not perfectly match the real terminal for complex TUI applications. Windows uses native screen buffer reads for exact fidelity.
- `send_input` escape sequences are interpreted by the worker; if the MCP client pre-processes backslashes (e.g. JSON `\r` → CR), the worker passes them through unchanged — both paths produce correct results.

## [0.3.0] - 2026-04-11

A quality-focused release built on top of the v0.2.0 foundation. pwsh is now stable and polished; bash / zsh / cmd are functional but lag on a few items. Drop-in upgrade from v0.2.0.

### Added
- **Syntax-highlighted AI command echo** — pwsh and Windows PowerShell 5.1 both render the echoed command with PSReadLine-equivalent colors: cmdlets, keywords (`foreach`, `in`, `if`, `else`, ...), scriptblock bodies (`Write-Host` inside `{ ... }`), double-quoted string interpolation (`"- $i"`), parameters, variables, numbers and comments. Hand-rolled state machine in `Services/PwshColorizer.cs` with unit tests.
- **Background busy / finished / closed reports** — every tool response now prepends a one-line summary of any other console's state, discovered on demand via a get_status pass. Includes a `✓ #N Name | Status: User command finished` line fired exactly once when a user-typed command like `pause` completes.
- **Source-cwd drift handling when auto-routing** — if the human user manually `cd`'d in the busy source console since your last command, splash preserves your last known cwd by using it as the cd preamble target on the routed-to console and attaches a one-line `Note: source #N was moved by user to '...'; ran in #M at your last known cwd '...'` to the response. Source's `LastAiCwd` is intentionally not updated, so later returns to that console still prompt a verify-and-retry warning.
- **Same-console drift warning** — if the user manually `cd`'d in the *idle* active console, the next `execute_command` returns a "verify cwd and re-execute" warning instead of running in the wrong place.
- **`wait_for_completion` three-state contract** — distinguishes "no commands pending" (nothing to wait for, stop calling), "completed" (one or more drained results included), and "still busy" (call again to keep waiting).

### Changed
- **NativeAOT publish** — `splash.exe` cold start dropped from ~1 s (R2R) to ~130 ms, eliminating the race between Claude Code's first MCP call and splash warm-up.
- **Two concurrent owned pipe listeners** — a long-running `execute_command` no longer stalls `get_status` / `get_cached_output`; the second instance stays free for status queries.
- **500 ms fixed settle removed** — fast commands return without the old delay; trailing output is drained adaptively via the new `drain_post_output` pipe command.
- **Stream capture rewritten** as OSC C/D position slicing (`_commandStart` / `_commandEnd`), replacing the layered AcceptLine noise filters and first-newline heuristics.
- **start_console banner / reason survive ConPTY startup** — they used to flash for ~0.5 s before ConPTY's initial `\e[?25l\e[2J\e[m\e[H` wiped them. For pwsh / powershell.exe the banner is now emitted from inside the shell via the generated integration tempfile, so it sticks.
- **Unowned window title** changed from `#PID ____` to `#PID ~~~~` so splash's idle state visually differentiates from PowerShell.MCP's identical `____`.
- **AI command echo blank line removed** — `cmdDisplay` no longer ends with `\r\n`, so it no longer doubles up with PSReadLine's AcceptLine newline.
- **Same-shell-family pinning when auto-switching** away from a busy console, so bash users don't get silently bounced into pwsh.
- **`powershell.exe` fallback** when `pwsh.exe` is absent on the host.
- **Tool descriptions refreshed** to reflect routing, cwd preservation, busy reports and `wait_for_completion` states — AI clients now see the new behavior in their tool list.

### Fixed
- **PSReadLine prediction and AcceptLine noise** leaking into pwsh command capture — pre-existing in v0.2.0, now cleanly avoided by moving OSC C emission to `PreCommandLookupAction` and slicing captured output between OSC C and OSC D.
- **First-OSC-B emission race** that stripped the first line of output on certain pwsh commands — the Enter handler now emits OSC B before delegating to `AcceptLine`.

### Known limitations (resolved in 0.4.0)
- ~~Multi-line commands break in ConPTY~~ — fixed in 0.4.0 via tempfile dot-sourcing.
- bash / zsh / cmd still use the pre-banner-fix `start_console` path, so banners flash briefly there. Colorization is pwsh-only.
- Routing / drift logic has no automated end-to-end test coverage yet.
- Worker re-claim across proxy restarts loses `LastAiCwd` / `LastAiCommand` state (expected).

## [0.2.0] - 2026-04-10

### Added
- **Claim-handshake version check** — a strictly newer proxy trying to attach to an older worker is refused; the old worker marks itself obsolete and stops serving pipes while keeping the PTY alive for the human user, so the MCP session disconnects cleanly without killing the shell.
- **npx-based install docs** — README now documents `npx splashshell` as the primary install path.

## [0.1.0] - 2026-04-10

First published release, rebranded from the internal `shellpilot` codename.

### Added
- **ConPTY backend** with shell integration via OSC 633 sequences (PromptStart / CommandInputStart / CommandExecuted / CommandFinished / Cwd).
- **Multi-shell support** — bash, pwsh, powershell.exe, cmd.exe can run simultaneously, each in its own worker process with its own Named Pipe.
- **Console re-claim** — worker survives proxy death and can be picked up by a new proxy on the unowned pipe so the human user never loses their shell when Claude Code restarts.
- **Per-console window titles** — `#PID Name` when owned, `#PID ____` when unowned.
- **Per-console cwd tracking** — `LastAiCwd` per console, auto cd on switch, detection of busy active console.
- **Banner and reason** display on `start_console`, with format-aware layout.
- **MCP tools**: `start_console`, `execute_command`, `wait_for_completion`, plus file tools (`read_file`, `write_file`, `edit_file`, `find_files`, `search_files`).
- **Cached output drain** on every MCP tool call so timed-out AI commands surface their result automatically.
- **Closed-console notifications** so the AI learns when a console has been closed since the last tool call.
- **User input forwarding** from the visible console to ConPTY, so the human can still type in the shared terminal (Ctrl+C, interactive prompts, etc).
- **OSC 0 window title preservation** against shell overrides.
- **Shell type + cwd** included in `start_console` response and in status lines.
