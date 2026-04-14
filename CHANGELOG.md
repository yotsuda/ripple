# Changelog

All notable changes to splash are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows [Semantic Versioning](https://semver.org/).

## [0.6.0] - 2026-04-15

Cache-on-busy-receive salvage layer plus a second Common Lisp adapter. When a command is in flight and the MCP client silently drops the response channel — ESC cancel, the MCP protocol's 3-minute ceiling, or a fresh tool call sneaking in on the same console — the worker flips the in-flight command to cache-on-complete mode so its eventual result lands in a per-console list instead of being silently discarded. The next tool call — **any** tool call, not just execute_command — drains the list and surfaces the result to the AI. Mirrors the PowerShell.MCP pattern, then closes three implementation holes observed in its reference. And: **Armed Bear Common Lisp** (ABCL) joins the embedded adapter set, giving splash a JVM-hosted Lisp reference point for the `balanced_parens` counter and proving the **Groovy pattern** (java.exe from a whitelisted Program Files path loading a jar payload from `%LocalAppData%`) works for any future JVM-hosted REPL. 536 / 536 test assertions pass (408 unit + 79 pre-existing E2E + 49 adapter-declared).

### Added
- **`CommandTracker.FlipToCacheMode()`** — detaches the in-flight TCS with a `TimeoutException` and marks `_shouldCacheOnComplete` so the eventual OSC-driven `Resolve()` appends to `_cachedResults` instead of delivering to the original caller. Invoked by two paths: the `CancellationTokenSource` registration firing at the 170 s preemptive deadline, and `HandleExecuteAsync` catching a fresh `execute_command` on a busy console (proof the prior caller stopped listening).
- **Multi-entry cached results per console** — `_cachedResults: List<CommandResult>` replaces the old single-slot `_cachedResult?` so sequential flipped commands accumulate without racing to overwrite. `ConsumeCachedOutputs()` drains the whole list atomically in one call, and `get_cached_output` returns a `results` array over the pipe protocol.
- **170 s preemptive timeout cap** — `CommandTracker.PreemptiveTimeoutMs = 170_000` and `ConsoleManager.MaxExecuteTimeoutSeconds = 170` clamp both the worker-side timer and the proxy-side pipe wait, so `execute_command` always returns a usable response within the MCP 3-minute tool-call window even when the underlying command keeps running in the background.
- **Worker-baked status line on cached results** — `CommandTracker.SetDisplayContext(displayName, shellFamily)` (populated from `claim` / `set_title`) lets the worker compute a self-describing status line at `Resolve` time. `CommandResult.StatusLine` carries it through the pipe, `ExecuteResult.StatusLine` carries it through the proxy, and `ShellTools.AppendCachedOutputs` prefers it over proxy-side reformatting so drained output reads identically to inline results — even if the console has since been reused.
- **84 new cache/drain test assertions** — 76 `CommandTrackerTests` unit tests for flip semantics, list accumulation, atomic drain, status-line formatting (pwsh / cmd / bash variants), cache survival across `RegisterCommand`, the 170 s cap, and a wall-clock preemptive-timer path; 8 `ConsoleWorkerTests` E2E assertions covering the multi-entry wire protocol — two back-to-back short-timeout commands stack in the cache and drain in a single RPC with both status lines intact, follow-up drain returns `no_cache`.
- **Armed Bear Common Lisp (ABCL) adapter** — `adapters/abcl.yaml` + `ShellIntegration/integration.abcl.lisp`. Second Common Lisp family adapter, first JVM-hosted Lisp shipped embedded. Runs ABCL 1.9.2 from `%LocalAppData%\splash-deps\abcl-bin-1.9.2\` via `java.exe` from Program Files — the same **Groovy pattern** that bypasses AppLocker's user-dir PE block by keeping the only spawned executable in a whitelisted location and loading the jar payload as classfiles. The integration script `setf`s `top-level::*repl-prompt-fun*` to an OSC-emitting wrapper — simpler than CCL's `ccl::print-listener-prompt` override which needs a kernel-redefine warning gate. Prompt is overridden from ABCL's default `CL-USER(N): ` to a literal `? ` so both CL adapters share mode regexes. `balanced_parens` / `char_literal_prefix` / tempfile delivery / `modes.main` mirror `ccl.yaml`. Probe + 5 declared tests (arithmetic, `defparameter` persistence, block comment `#| |#`, char literal `#\(`, modes-main default) all pass on `--adapter-tests --only abcl`. Debugger mode intentionally deferred because ABCL's `system::debug-loop` uses a separate prompt mechanism that needs research against a real break scenario.
- **`--adapter-tests [--only <name>]` CLI flag** — runs each adapter's declared `tests:` block standalone, without the `ConsoleWorkerTests.Run` harness whose pre-existing Ctrl+C / obsolete-state flakes hard-exit the process on failure and would otherwise mask downstream adapter-declared results. Useful after adding a new adapter to verify just its own tests in isolation (e.g. `--adapter-tests --only abcl`) or to run the full declared-test suite across all loaded adapters independently of the broader `--test --e2e` gate.

### Changed
- **`execute_command` timeout cap** — the tool's `timeout_seconds` still defaults to 30 but now hard-caps at 170 s; larger values are silently clamped. Worker-side `RegisterCommand` applies the same cap internally so the pipe wait and worker timer both unwind inside the MCP 3-minute window.
- **`CollectCachedOutputsAsync` / `WaitForCompletionAsync` drain loops** — both consume the new `results` array from `get_cached_output` and emit one `ExecuteResult` per cached entry. A console that accumulated three flipped results surfaces as three entries in the next tool response.
- **`AppendCachedOutputs` in every MCP tool** — `send_input` is now wrapped like the rest, so its response also drains any cached results on its target console and reports other consoles' busy / finished / closed state. Closes the last gap where a tool response could omit freshly-ready cached output.

### Fixed
- **Drain hole: read-only MCP tools didn't surface stale cache** — PowerShell.MCP's `CollectAllCachedOutputsAsync` is only called from execute / wait_for_completion handlers, so tools like `get_current_location` leave other consoles' caches sitting until the next execute. splash now drains from every tool response (execute, wait_for_completion, start_console, peek_console, send_input), matching the user's "any MCP tool response" requirement.
- **Drain hole: older cache hidden behind timeout / flipped branches** — PS.MCP's `invoke_expression` timeout / `shouldCache` branches don't consume older cache entries, so they sit until the next normal completion. splash's atomic `ConsumeCachedOutputs` picks up everything in one call regardless of which branch fires.
- **`CancellationTokenRegistration` self-disposal deadlock** — `FlipToCacheMode` originally disposed `_timeoutReg` inline, but when called FROM the token's own callback via `Register(FlipToCacheMode)`, `CTR.Dispose` blocks until the callback finishes — which was the same thread currently inside the callback. Disposal now happens in `Resolve`'s cache branch and `AbortPending`, where the command has already finished running.

### Known limitations
- **Silent fast-completing ESC cancel** — if `execute_command` completes normally in well under 170 s but the client already stopped listening (ESC fired before anything triggered a flip), the result is delivered via `_tcs.TrySetResult` and never enters the cache. splash has no way to detect client-side cancel without a protocol extension. Commands taking more than a few hundred milliseconds are covered via the flip-on-busy-receive trigger as soon as the next tool call arrives.
- **Cross-agent salvage not attempted** — the drain walks only consoles owned by the current agent. A sub-agent's flipped cache is not visible to the parent agent's tool calls, by design (agent isolation is a first-class splash concept).

## [0.5.0] - 2026-04-13

A round of cmd.exe and bash polish driven by systematic shell-by-shell testing. cmd is now usable for AI commands instead of hanging indefinitely; bash subshells and command substitutions resolve correctly; pwsh integration tolerates a missing PSReadLine module without crashing the worker. 240 / 240 tests pass.

### Added
- **cmd.exe AI command support** — multi-line cmd commands (heredoc-equivalent batch blocks, `if/else`, `for /l`, `setlocal enabledelayedexpansion` with `!VAR!`) now work via a tempfile `.cmd` wrapper called from a single-line PTY input. ConPTY's input echo is stripped from the captured slice via `StripCmdInputEcho` so AI output mirrors what pwsh and bash produce.
- **cmd.exe user-busy detection** — a side-channel polling loop (cmd worker only, Windows only) samples the cmd process's CPU time delta and child-process count every 500 ms. CPU > 50 ms / 500 ms catches builtins like `dir /s`; child detection via `CreateToolhelp32Snapshot` catches external commands like `notepad`, `git`, `xcopy`, `timeout /t`. Either signal flips the tracker to busy so `execute_command` auto-routes around the user. Suppressed during AI command execution.
- **Multi-shell E2E test suite** — `RunMultiShell` exercises pwsh / Windows PowerShell 5.1 / cmd / bash through the worker pipe protocol with shell-specific `ShellProfile`s. Covers ready/standby state, simple echo with input-echo strip assertion, session variable persistence, multi-line block syntax, and (bash) subshell capture + exit-code propagation. `RunIntegrationScriptGuardTest` verifies `integration.ps1` doesn't crash when PSReadLine is unloaded.
- **Additional E2E tests for pwsh** — session variable persistence across execute calls, multi-line foreach, slow-command timeout + busy-state probe, cached output retrieval after timeout, `send_input` rejection on idle consoles, `send_input` Ctrl+C interrupt with standby recovery.

### Changed
- **bash integration rewritten from DEBUG trap to PS0** — `PS0=$'\\e]633;C\\a'` fires OSC 633 C exactly once per command-line submit in the parent shell, working for subshells (`(echo foo)`), command substitutions (`$(date)`), pipelines, brace groups, and multi-statement lines. The old DEBUG trap approach couldn't fire for compound commands without `set -T`, and even with functrace had recursive emission issues inside `__sp_precmd`. The `__sp_in_command` flag and DEBUG trap are deleted entirely; `__sp_precmd` now emits OSC D unconditionally.
- **bash multi-line / multi-statement command capture** — multi-line bodies route through a tempfile `.sh` dot-source with WSL/MSYS path translation. Multi-statement single-line commands (`cmd1; cmd2; cmd3`) capture all output in order — previously only the last statement was reported because each sub-command's DEBUG firing reset the OSC C marker.
- **cmd.exe status line** — now renders as `○ Finished (exit code unavailable)` instead of a misleading `✓ Completed`. cmd's PROMPT can't expand `%ERRORLEVEL%` at display time, so the worker reports a fake exit 0 for every command; the new status text makes that limitation visible to the AI instead of silently lying.
- **Long status-line commands truncated** — the pipeline column in status lines now caps at 60 characters with `...` to keep multi-line responses readable.

### Fixed
- **cmd.exe AI commands hung forever** — cmd has no preexec hook to fire OSC 633 C, so the proxy tracker waited on a `_commandStart` that never advanced. The worker now calls `SkipCommandStartMarker()` after `RegisterCommand` for cmd, and cmd's PROMPT emits a fake `OSC 633 D;0` so the resolve path completes.
- **bash subshell commands hung forever** — `(echo foo)`, `(exit N)`, command substitutions all blocked the AI tracker indefinitely (and the multi-statement gate before that fix only captured the last sub-command's output). The PS0 rewrite resolves both issues.
- **bash multi-line newlines lost in PTY echo** — embedded `\n` in execute payloads got submitted as Enter and dropped subsequent lines into the continuation prompt. The tempfile-dot-source path preserves the body as a single source file.
- **pwsh integration crash when PSReadLine missing** — `Set-PSReadLineKeyHandler` and `Set-PSReadLineOption` calls are now guarded by `Get-Module PSReadLine` plus per-call `try/catch` so a screen-reader fallback or `Remove-Module` doesn't throw `CommandNotFoundException` at integration-load time.

### Known limitations
- **cmd.exe exit codes are always reported as 0**. cmd's PROMPT can't expand `%ERRORLEVEL%` at display time, so the worker emits a fake `OSC 633 D;0` after every command. AI commands show as `Finished (exit code unavailable)` to make the limitation visible. Use pwsh or bash if you need exit-code-aware execution. (The visible terminal still has the real `%ERRORLEVEL%`; only the AI-side capture is affected.)
- **`Remove-Module PSReadLine` mid-session breaks the pwsh worker.** PSReadLine spawns persistent reader threads that survive module unload (.NET can't fully unload binary modules), so the orphaned threads keep consuming console input bytes and the next AI command hangs forever. Splash can't recover from this state. Documented in README.
- **cmd.exe builtin interactive prompts are not detected as user-busy** (`pause`, `set /p`). Zero CPU + zero children leave both polling signals silent. Uncommon enough to leave undetected.

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
