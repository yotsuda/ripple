# Changelog

All notable changes to splash are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows [Semantic Versioning](https://semver.org/).

## [0.8.0] - 2026-04-16

First round of **debugger adapters** plus three new REPL adapters and
two root-cause fixes that together close out the user-input-
contamination class of flakes. The adapter schema gains three
additive fields (`process.executable_candidates`,
`modes.advance_commands`, `commands.debugger`) that let a single
adapter YAML describe any debugger's step / print / breakpoint
vocabulary in a vendor-agnostic way — AI agents drive perldb and
jdb using the same operation names, no per-debugger knowledge
required. **18 embedded adapters** (up from 12), with perldb / jdb /
pdb the first members of the `family: debugger` class. `--adapter-
tests` runs 100 assertions green; window creation during the test
suite is fully silent (test workers launch with SW_HIDE) so a long
test run no longer disrupts the user's other windows.

### Added
- **`family: debugger` adapter framework.** The `family` enum value
  `debugger` moves from declarative-only (CCL's break-loop mode +
  python's pdb mode, both inherited inside a REPL) to a first-class
  adapter type with three independently-verified instances:
  - **`perldb`** — Perl's `perl -d -e 0` scriptless debugger. Prompt
    `  DB<N> ` with nested form `  DB<<N>> ` fired on breakpoint
    pause. Regex strategy + init none. 8 adapter tests, including an
    end-to-end breakpoint-hit / inspect-parameter / continue /
    verify-return chain.
  - **`jdb`** — Java Debugger detached mode (`jdb` with no target
    class). Prompt `> `. 5 adapter tests covering deferred
    breakpoint registration, meta-command dispatch, and detached-
    mode restrictions.
  - **`pdb`** — Python's built-in debugger via
    `python -c "import pdb; pdb.Pdb().set_trace()"`. Prompt
    `(Pdb) `. 6 adapter tests.
- **`process.executable_candidates`** (schema §3). Ordered list of
  launcher binaries tried left-to-right with `%VAR%` env-var
  expansion; first entry that resolves to an existing file wins.
  Solves the "single absolute path doesn't port across distributions"
  problem for interpreters with multiple plausible install locations
  (Strawberry / ActivePerl / Git-bundled perl; Microsoft OpenJDK /
  Temurin / Corretto / Zulu; python.org / Windows Store / Anaconda).
  Falls back to the legacy `executable` field, then the adapter name.
- **`commands.debugger`** (schema §10). Structured debugger-operation
  vocabulary: navigation (`step_in`, `step_over`, `step_out`,
  `continue`, `run`), inspection (`print`, `dump`, `backtrace`,
  `source_list`, `locals`, `where`, `args`), breakpoints
  (`breakpoint_set`, `breakpoint_set_line`, `breakpoint_list`,
  `breakpoint_clear_all`). Each field is a command template string
  with `{expr}` / `{target}` / `{line}` / `{file}` placeholders, or
  `null` when unsupported. AI agents discover a debugger's syntax
  from the adapter instead of parsing help text.
- **`modes.advance_commands`** (schema §9). Distinct from
  `exit_commands`: advance commands (step_in / step_over / step_out)
  change position within the same paused mode without leaving it.
  AI agents use this to distinguish "I stepped one line but I'm
  still paused" from "I resumed and left the breakpoint".
- **`sqlite3` REPL adapter.** Launches `sqlite3 :memory:` with
  `.mode list` + `.headers off` forced at startup so query output
  is pipe-separated and regex-friendly (sqlite 3.33+ defaults to a
  pretty "box" render otherwise). Dot-commands documented via
  `commands.builtin`. 6 tests.
- **`lua` REPL adapter.** Lua 5.4 interactive interpreter with the
  classic `> ` prompt. Uses the `=` prefix shortcut (Lua 5.3+) in
  probes to return values without wrapping in `print()`. 6 tests.
- **`deno` REPL adapter.** Deno 2.x for JavaScript / TypeScript
  evaluation. Distinct from the node adapter — Deno evaluates
  TypeScript directly (`const x: number = 42` works without
  ts-node), supports top-level await natively, and has built-in
  Web Platform APIs. `NO_COLOR=1` in the adapter env disables
  ANSI colorization for clean regex matching. 6 tests.
- **`--no-user-input` worker flag.** Test workers launched via
  `AdapterDeclaredTestsRunner` now pass this flag so their
  `InputForwardLoop` permanently holds (suppresses) user
  keystrokes. Prevents the user's typing in unrelated windows from
  leaking into test workers' PTYs and corrupting probe commands —
  the root cause of the intermittent jshell / node / fsi /
  jdb-hello flakes on the 0.7 release train.
- **OSC sequence stripping in `RegexPromptDetector`.**
  `StripCsiWithMap` now consumes `ESC ] ... BEL` and `ESC ] ... ESC \`
  in addition to CSI. Fixes the class of failures where ConPTY's
  window-title setter (`ESC ] 0 ; <path> BEL`) emitted right after
  process launch sat between the banner and the prompt, preventing
  `^` anchoring. 3 regression tests in
  `RegexPromptDetectorTests`.

### Changed
- **User input is now held during AI command execution.** A new
  `_holdUserInput` gate in `ConsoleWorker` causes `InputForwardLoop`
  to buffer keystrokes into `_heldUserInput` instead of forwarding
  them to the PTY while `HandleExecuteAsync` is in flight. Held
  bytes replay automatically after the command completes (success /
  timeout / error). Ctrl+C (0x03) passes through even when held so
  the user can always interrupt a stuck command. Operates at
  splash's own forwarding layer (above the shell), making it
  universal across adapters regardless of whether the shell has a
  line editor — the legacy `input.clear_line` path is retained as
  belt-and-suspenders for characters that slipped into the line
  editor before the gate was set.
- **`--adapter-tests` worker console windows are hidden (SW_HIDE).**
  Normal splash usage keeps `SW_SHOWNOACTIVATE` so the shared
  console is visible-but-inactive; test runs gate on `noUserInput`
  to switch to fully invisible windows. Rapid window creation
  during a full test suite (15+ workers) previously caused focus
  churn that disrupted the user's other windows — now the entire
  test run is silent.

### Fixed
- **`RegexPromptDetector` missed prompts behind OSC window-title
  sequences.** Pre-0.8 the stripper handled CSI (`ESC [ ... <letter>`)
  only, leaving OSC sequences intact in the stripped text. When
  ConPTY emitted `ESC ] 0 ; <path-to-executable> BEL` right before
  the first prompt (standard terminal behaviour for child-process
  launches), the title bytes sat between the banner newline and
  the prompt, and a regex like `^> $` could not anchor. The jdb
  adapter's initial-prompt detection blocked on this exact pattern
  (commit `0a98d31`). Fix extends `StripCsiWithMap` with an OSC
  branch that consumes the sequence entirely via BEL or ST
  terminator.
- **`CS8600` compiler warning in
  `ConsoleWorker.HandleExecuteAsync`.** `ModeMatch` is a record
  (reference type) so `ModeMatch match = default` gave null and
  triggered flow-analysis warnings at the post-loop `match.Name`
  access. The `while(true)` loop always reassigns before the
  post-loop read, but the compiler can't prove it. Fix (commit
  `f2f3834`): replace `default` with an explicit
  `new ModeMatch(Name: null, Level: null)` sentinel so the
  variable is never null and the safety invariant becomes a
  constructor invocation rather than an unprovable loop property.
- **jdb adapter's `next`-stepping test** was state-dependent on
  a previous test leaving the VM paused at the breakpoint. The
  state-inheriting design is fragile — the `cont` async race (jdb
  returns the post-resume prompt immediately while the VM is still
  running, so `Result: 42` stdout arrives after the test runner
  has already declared the command complete) is also documented
  in the adapter YAML's comments for future `async_interleave`
  work.

### Known limitations
- **`dotnet-dump analyze` is post-mortem only.** Shipping an
  adapter for it has the same fixture-dependency problem as
  `jdb-hello`: a dump file must exist at adapter-launch time.
  Deferred until there's a concrete workflow that needs dump-
  analysis integration.
- **`IPython` prompt detection under ConPTY.** IPython's
  `--simple-prompt` mode still emits absolute cursor positioning
  (`\x1b[5;1H`) when it detects a TTY-like environment. `TERM=dumb`
  and `PROMPT_TOOLKIT_NO_CPR=1` don't override this — it's a
  ConPTY-specific codepath inside IPython. No IPython adapter
  ships in 0.8; the stdlib `python` adapter remains the
  recommended Python REPL.
- **Adapters using `b subname` on `do`-loaded subs** (perl5db).
  Setting a function-name breakpoint on a sub that was loaded from
  an external file via `do 'file.pl'` may silently not fire — the
  address resolution doesn't always match the file-loaded code.
  Workaround documented in `perldb.yaml`: use line-number
  breakpoints (`b {line}`) after `l subname` finds the target lines.

## [0.7.0] - 2026-04-15

Polish round + shipping the CCL adapter. Seven bug fixes found via
adversarial testing of the v0.6.0 surface (window title split-chunk
leak, nested datum comments, node/groovy `signals.interrupt`
mis-declaration, console focus theft, line-editor buffer flush,
mode detection against the wrong input source), plus **12 embedded
adapters** for the first time: `ccl` moves out of the local gitignore
after empirical confirmation that the corporate AppLocker block which
motivated the exclusion has been relaxed. Each fix started with a
complaint or a suspected weakness, pinned the broken behaviour with
a test, then replaced the implementation. No architecture rewrites;
the worker / proxy split and the adapter-YAML schema stayed stable
across all seven. **528 assertions pass** on `--test` (458 unit) +
`--adapter-tests` (70 declared, 12 adapters). The two pre-existing
`ConsoleWorkerTests.Run` flakes (Ctrl+C standby, obsolete PTY alive)
that block `--test --e2e` from reaching the declared suite also
predate 0.6 and are tracked separately — they're invisible to
release binaries.

### Fixed
- **Owned console window titles sometimes got clobbered by the
  shell.** The read-loop's `ReplaceOscTitle` was stateless and
  couldn't handle an OSC 0/1/2 title sequence that straddled a PTY
  read-chunk boundary — the partial opener leaked to the visible
  terminal and the terminal interpreted bytes 1..N as the shell's
  title up to whatever terminator eventually arrived, clobbering
  splash's desired title. Fix (commit `737f0a3`): add a
  `ref string pendingTail` parameter so the unterminated opener is
  buffered on `_oscTitlePending` and reassembled on the next chunk.
  37 new unit assertions cover split points between `\e` and `]`,
  between `]` and the type byte, mid-body, right at the terminator,
  at the ST two-byte terminator, and with non-title OSCs
  (633, 7, 112) flowing through untouched.
- **`#;#;(a)` reported complete when it needed a second datum.**
  The BalancedParensCounter's atom-run-consume branch was
  decrementing `pendingDatumComments` on atoms inside a
  datum-commented list — atoms that were already being skipped by
  the list's own bracket accounting. Stacked datum-comment
  prefixes with only one following datum therefore silently
  resolved and the counter reported "submit-ready" when it should
  have held. Fix (commit `39b6a83`): gate the atom-run branch on
  `datumCommentAnchorDepths.Count == 0` so inner atoms don't
  resolve outer prefixes. 32 new stress assertions harden the
  counter against reader-macro pathologies: char literals of
  quote / semicolon / pipe / backslash, bracket type mismatch
  (pinned as a known gap), multi-line strings, 200-deep nesting,
  quasi-quote, CL `#+nil` passthrough, and the fixed nested
  datum-comment scenarios.
- **Node / groovy mis-declared `signals.interrupt`.** Live
  verification of all 10 adapters' interrupt handlers found two
  that lied about their capability: Node's REPL cannot handle
  Ctrl-C while its event loop is blocked by a sync JS loop or
  pending top-level await (the signal handler runs on the same
  thread), and groovysh's Ctrl-C is destructive — it terminates
  the JVM and closes the shell outright. Fix (commit `f19f3c1`):
  flip `capabilities.interrupt` to `false` for both, set
  groovy's `signals.interrupt` to `null` so MCP clients don't
  even try `send_input "\x03"`, and extend SCHEMA §11 with
  nullable-interrupt semantics. Also: SignalsSpec.Interrupt is
  now `string?` to support explicit `null` in YAML.
- **New console windows stole keyboard focus.** `CreateProcessW`
  with only `CREATE_NEW_CONSOLE` gives the new console active-
  window status by default, so starting a splash shell while
  the user is typing in their editor drops their keystrokes
  into splash's buffer. Fix (commit `c90d3f1`): set
  `STARTF_USESHOWWINDOW | wShowWindow = SW_SHOWNOACTIVATE` in
  `STARTUPINFOW` so the new window is displayed but not
  activated. The user's editor keeps focus.
- **User-typed bytes were prepended to the next AI command.** Even
  with the focus fix, users occasionally click into a splash
  console and type a few keystrokes before noticing. Without a
  flush, those bytes sit in the shell's line-editor buffer and
  get submitted together with the next AI command as one garbled
  line. Fix (same commit `c90d3f1`): new `input.clear_line`
  schema field carries the bytes to write before each execute
  to wipe the current line. Default is `null` (opt-in per
  adapter) because Python `PYTHON_BASIC_REPL=1`, fsi
  `--readline-`, Racket `-i`, CCL, and ABCL deliberately run
  without a line editor and would parse the obvious `\x01\x0b`
  (Ctrl-A + Ctrl-K) as literal input — empirical probe against
  Python basic REPL produced
  `SyntaxError: invalid non-printable character U+0001`. Opted
  in for bash and zsh where readline / ZLE emacs defaults
  handle the bytes as no-ops on an empty buffer. Eight schema
  pins in AdapterLoaderTests lock the per-adapter expectations.
- **`ModeDetector` never saw the post-OSC-A prompt, so every
  mode transition silently fell through to the default.** The
  auto_enter + nested + level_capture machinery was scanning
  `cleanedOutput` (the OSC-C..D slice) for the mode's detect
  regex. But the mode transition signal lives in the NEXT
  prompt — `1 > ` for CCL's break loop, `(Pdb) ` for Python,
  `N] ` for SBCL — which arrives AFTER OSC A fires Resolve, so
  `cleanedOutput` literally can never contain it. And the
  obvious alternative (`GetRecentOutputSnapshot()`) routes the
  ring through VtLite, which reshapes the trailing prompt into
  cell-addressed coordinates that break `^<prompt>$` anchored
  regexes. Fix (commit `ca78f95`): scan `GetRawRecentBytes()`
  in a short 150 ms poll loop, breaking out as soon as a
  non-default auto_enter mode matches. Verified empirically
  against a local 4-test chain walking CCL's break loop
  (`(error ...)` → `1 >` → nested → `2 >` → `:pop` → `1 >` →
  `:pop` → main). Schema §18 Q2 ("auto_enter + nested +
  level_capture at runtime") is now backed by runtime evidence
  rather than just ModeDetector's unit tests.

### Added
- **Clozure Common Lisp (CCL) adapter ships embedded.** Through
  v0.6.0 `adapters/ccl.yaml` + `ShellIntegration/integration.lisp`
  lived locally-only because corporate AppLocker on the dev box
  blocked user-dir PE files under ConPTY spawn — earlier adapter
  tests consistently hit `CreateProcessW failed: 5`. On
  2026-04-15 that block is empirically gone: `--adapter-tests
  --only ccl` runs 10 / 10 green across repeated runs, covering
  the probe, five expression-level tests (arithmetic, stateful
  defparameter, block-comment reader macro, char-literal reader
  macro, default mode), and the four-test debugger-mode chain
  that was added this round to verify §18 Q2's auto_enter +
  nested + level_capture path (`(error ...)` → `1 > `, nested
  → `2 > `, `:pop` → `1 > `, `:pop` → main). CCL is now the
  first native-binary Lisp in the embedded set alongside the
  JVM-hosted ABCL. On boxes where the AppLocker block persists
  the probe will still soft-fail — same class as a missing
  zsh on Windows, not a regression.
- **`--adapter-tests [--only <name>]` CLI flag** — already shipped
  in 0.6.0 but now documented as the canonical way to exercise
  adapter-declared tests without the pre-existing E2E flakes in
  `ConsoleWorkerTests.Run` hard-exiting the process. Useful after
  adding a new adapter to verify just its own tests in isolation.
- **`input.clear_line` schema field** — documented in SCHEMA.md §8
  alongside the empirical-verification requirement ("walk the
  adapter in splash, type into its console window, run
  execute_command, confirm the clear bytes wipe the buffer
  without syntax errors, then add the field"). Bash and zsh opt-in;
  everything else null by default.
- **ABCL 1.9.2 adapter gotcha** in HANDOFF.md — the `--add-opens
  java.base/java.lang=ALL-UNNAMED` flag on the Groovy-pattern
  command template silences the JDK 21 virtual-threading
  introspection warning that ABCL 1.9.2 prints on every cold
  start.
- **Pre-existing E2E flake documentation** — HANDOFF.md documents
  the two `ConsoleWorkerTests.Run` tests (Ctrl+C post-interrupt
  standby, obsolete PTY alive) that have been failing across
  sessions and the `--adapter-tests` standalone workaround. On
  release binaries the flakes are invisible — they only affect
  the `--test --e2e` contract gate during development.

### Changed
- **`SignalsSpec.Interrupt` is now `string?`.** The YAML default
  stays `"\x03"` for adapters that omit the field, but adapters
  with destructive Ctrl-C handlers (groovy today, future hosts
  that kill the process on Ctrl-C) can now set `interrupt: null`
  in YAML to signal "no safe interrupt byte available".
- **Mode detection poll window.** `ConsoleWorker.HandleExecuteAsync`
  now waits up to 150 ms for a non-default auto_enter mode to
  appear in the raw ring after a command resolves. Happy path
  (default mode) returns immediately; transitions that need the
  post-A prompt bytes to arrive get up to 150 ms of headroom.
- **`capabilities.interrupt`** for node and groovy flipped to
  `false`. MCP clients querying the flag now see the honest
  story: sending Ctrl-C to either host will NOT rescue a runaway
  command.

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
