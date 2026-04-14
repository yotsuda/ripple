# splash adapter schema — v1 draft

Declarative description of an interactive process so splash can drive it over a
PTY with the same runtime. Covers **shells** (pwsh, bash, zsh, cmd, fish, nu…)
and **REPLs** (python, node, clojure, ghci, sbcl, iex…) under one contract.

Status: **draft**. Not frozen. Expect breaking changes until `schema: 1` is
stamped on a shipped splash release.

---

## 1. Design principles

1. **Declarative, not procedural.** Every adapter is data. The runtime knows
   how to drive a PTY; the adapter just tells it *what* strings to send and
   *what* patterns to look for.
2. **Marker-first prompt detection.** Regex-based prompt matching is a fallback.
   The primary strategy is to inject a unique marker string (OSC 633 for shells,
   `\u0001SPLASH\u0001` for REPLs) at startup so the runtime can locate prompt
   boundaries without risking false positives from command output.
3. **Exec-form commands only.** `process.command_template` is expanded with
   named placeholders. Shell-interpolated strings are forbidden — no quoting
   holes, no injection.
4. **Versioned.** Every adapter declares `schema: 1`. Future versions are
   additive-first; breaking changes bump the schema major.
5. **Testable.** Every adapter ships its own contract tests in `tests:`. CI
   runs them against the declared interpreter binary before merge.

---

## 2. Top-level fields

| Field | Required | Type | Purpose |
|---|---|---|---|
| `schema` | yes | int | Schema major version. Currently `1`. |
| `name` | yes | string | Canonical short name (e.g. `pwsh`, `python`). Used for adapter lookup. |
| `version` | yes | semver | Adapter file version (independent of interpreter version). |
| `description` | yes | string | One-line human description. |
| `homepage` | no | URL | Upstream project URL. |
| `license` | no | SPDX | License of the underlying interpreter (not of the YAML). |
| `family` | yes | enum | `shell` \| `repl` \| `debugger`. Affects defaults and UI labeling. |
| `aliases` | no | [string] | Additional names this adapter responds to (e.g. `powershell` for pwsh). |
| `process` | yes | object | How to launch the process. See §3. |
| `ready` | yes | object | How to detect that the process is ready for input. See §4. |
| `init` | yes | object | Integration injection strategy. See §5. |
| `prompt` | yes | object | Prompt detection strategy. See §6. |
| `output` | yes | object | Output post-processing. See §7. |
| `input` | yes | object | Input delivery strategy. See §8. |
| `modes` | no | [object] | REPL modes (Julia pkg, SBCL debugger, iex pry). See §9. |
| `commands` | no | object | Meta-commands / helpers. See §10. |
| `signals` | yes | object | Signal bytes (interrupt, eof, suspend). See §11. |
| `lifecycle` | yes | object | Shutdown and restart policy. See §12. |
| `capabilities` | yes | object | Feature flags. See §13. |
| `probe` | yes | object | Single sanity-check eval. See §14. |
| `tests` | yes | [object] | Contract tests run by CI. See §15. |
| `integration_script` | no | string | Inline script body (alternative to external `script_resource`). See §5. |

---

## 3. `process` — launch spec

```yaml
process:
  command_template: '"{shell_path}" -NoExit -Command "{init_invocation}"'
  inherit_environment: false
  env:
    POWERSHELL_TELEMETRY_OPTOUT: "1"
  encoding: utf-8
  line_ending: "\r"
```

- **`command_template`** — string template with `{placeholders}`. The runtime
  substitutes `{shell_path}` (resolved via PATH), `{init_invocation}` (expanded
  from `init`), `{tempfile_path}`, `{pid}`, `{guid}`, `{temp_dir}` as needed.
- **`inherit_environment`** — if `false`, the runtime calls
  `CreateEnvironmentBlock(bInherit=false)` (Windows) / `env -i` (Unix) so the
  child sees only the OS-default environment. pwsh uses this to avoid
  inheriting MCP server variables; bash/zsh need `true` on Windows because
  MSYS2/Git Bash require `HOME`, `PATH`, `MSYSTEM` from the parent.
- **`env`** — additional environment variables (merged on top of inherited).
- **`encoding`** — stdin/stdout encoding. Always `utf-8` in v1.
- **`line_ending`** — bytes to append when writing a line of input. pwsh/cmd
  use `\r` (ConPTY cooked-mode translates to CRLF), bash/zsh use `\n`.

---

## 4. `ready` — startup detection

```yaml
ready:
  wait_for_event: prompt_start
  timeout_ms: 0
  settle_before_inject_ms: 2000
  suppress_mirror_during_inject: true
  kick_enter_after_ready: true
  delay_after_inject_ms: 500
  output_settled_min_ms: 2000
  output_settled_stable_ms: 1000
  output_settled_max_ms: 30000
```

- **`wait_for_event`** — `prompt_start` | `marker` | `regex` | `custom`. For
  shell-integration adapters this is always `prompt_start` (the first OSC A
  event). For REPL adapters it's typically `marker`.
- **`timeout_ms`** — `0` means wait indefinitely. Recommended for interpreters
  with cold-start costs (pwsh + PSReadLine + Defender first-scan can take
  several seconds).
- **`settle_before_inject_ms`** — quiet period before injecting the integration
  script. Only meaningful when `init.delivery: pty_inject`.
- **`suppress_mirror_during_inject`** — hide the `source` echo from the visible
  console during injection.
- **`kick_enter_after_ready`** — send an Enter keystroke once ready to force a
  fresh prompt redraw (needed for shells whose initial prompt was suppressed
  by injection).
- **`output_settled_{min,stable,max}_ms`** — tuning knobs for the worker's
  `WaitForOutputSettled` drain phase. `min_ms` is the absolute minimum wait
  before polling starts; `stable_ms` is the consecutive quiet window
  required to declare output settled; `max_ms` is the hard deadline.
  Defaults 2000 / 1000 / 30000 match the pre-schema hardcoded behavior.
  Slow-compiler REPLs (Lisp, Haskell) may need to raise `max_ms`; fast
  REPLs can lower `min_ms` to speed startup.

---

## 5. `init` — integration injection

```yaml
init:
  strategy: shell_integration | marker | prompt_variable | regex | none
  hook_type: prompt_function | preexec | ps0 | precommand_lookup_action | debug_trap | custom | none
  delivery: launch_command | pty_inject | none
  script_resource: integration.ps1   # file under ShellIntegration/
  # -- OR inline:
  # script: |
  #   $global:__sp_pending ...
  init_invocation_template: "..."
  tempfile:
    prefix: .splash-integration-
    extension: .ps1
  banner_injection:
    mode: prepend_to_tempfile | write_before_pty | none
    banner_template: |
      Write-Host '{banner}' -ForegroundColor Green
    reason_template: |
      Write-Host 'Reason: {reason}' -ForegroundColor DarkYellow
  inject:
    method: source_file
    windows: { ... }
    unix: { ... }
  marker:                           # strategy: marker (REPL path)
    primary:      "\u0001SPLASH\u0001>>> "
    continuation: "\u0001SPLASH\u0001... "
```

**Strategy values** determine which sub-fields are relevant:

| Strategy | Who uses it | Required subfields |
|---|---|---|
| `shell_integration` | pwsh, bash, zsh | `script_resource` or inline `script`, `hook_type`, `delivery` |
| `prompt_variable` | cmd | `process.prompt_template` |
| `marker` | python, node, ghci, sbcl, iex | `marker.primary`, optional `marker.continuation`, optional `script` |
| `regex` | REPLs where PS1 can't be replaced | `prompt.primary_regex` |
| `none` | trivial processes with no setup | — |

**`hook_type`** documents *when* the OSC C marker (or equivalent
"command-about-to-execute" signal) fires, relative to the command pipeline.
This matters because it determines whether input echo can be cleanly separated
from command output:

- `prompt_function` — only prompt-time hook (cmd, old shells)
- `preexec` — zsh, fish
- `ps0` — bash (reliable since bash 4.4)
- `precommand_lookup_action` — pwsh (fires inside the engine before resolution)
- `debug_trap` — legacy bash (`DEBUG` trap — has subshell-visibility pitfalls)
- `custom` — adapter uses a strategy not in this enum
- `none` — no preexec hook available (cmd); use deterministic input-echo stripping

**`delivery`** determines how the integration script reaches the shell:

- `launch_command` — passed as part of the process command line (e.g.
  pwsh's `-NoExit -Command ". '{path}'"`). Runs before the first prompt.
- `pty_inject` — written to PTY stdin after the shell has printed its welcome
  banner. Used by bash/zsh because shell command-line args don't let us source
  arbitrary scripts silently.
- `none` — no external script; integration is entirely declarative (cmd's
  `prompt` variable carries the OSC sequences).

---

## 6. `prompt` — prompt detection

```yaml
prompt:
  strategy: shell_integration | marker | regex
  shell_integration:
    protocol: osc633
    markers:
      prompt_start: "\x1b]633;A\x07"
      command_input_start: "\x1b]633;B\x07"
      command_executed: "\x1b]633;C\x07"
      command_finished: "\x1b]633;D\x07"
      property_update: "\x1b]633;P\x07"
    property_updates:
      cwd_key: Cwd
  # -- OR for marker strategy:
  # primary: '^\u0001SPLASH\u0001>>> $'
  # continuation: '^\u0001SPLASH\u0001\.\.\. $'
  # group_captures:
  #   - { name: counter, group: 1, type: int, role: monotonic_counter }
```

**OSC 633 event contract (shell_integration strategy):**

The runtime guarantees strict event ordering per command:

```
A → (user typing, or AI write) → B → C → <output> → D;{exit_code} → P;Cwd=... → A
```

- `A` = prompt rendered, shell ready for input
- `B` = Enter pressed / line submitted
- `C` = command about to execute (boundary between input echo and output)
- `D;N` = command finished with exit code N
- `P;Cwd=...` = property update (currently only `Cwd` is defined)

Adapters that cannot emit OSC C (like cmd) must use
`output.input_echo_strategy: deterministic_byte_match` and accept that the
runtime will strip echo by walking the output stream.

**`group_captures.role`** — semantic tag for regex capture groups used by
REPL adapters:

- `monotonic_counter` — IPython `In [N]:`, iex `iex(N)>`
- `nesting_level` — SBCL debug level `N]`
- `mode_indicator` — irb nesting depth

---

## 7. `output` — output post-processing

```yaml
output:
  post_prompt_settle_ms: 150
  strip_ansi: false
  strip_input_echo: true
  input_echo_strategy: osc_boundaries | deterministic_byte_match | none
  line_ending: "\r\n"
  async_interleave:
    strategy: redraw_detect | quiesce | accept | none
    capture_as: out_of_band | merge | discard
```

- **`post_prompt_settle_ms`** — how long to wait after `A` fires before
  declaring the command's output complete. Shells vary: pwsh ~0, bash ~50,
  cmd ~400 (Format-Table trailing rows, PSReadLine prediction, etc.).
- **`strip_ansi`** — whether to remove non-OSC-633 ANSI escape sequences. For
  shell adapters we keep them so the visible console stays colorized; for
  REPL adapters we usually strip them before regex matching.
- **`input_echo_strategy`** — how to separate command input echo from real
  output:
  - `osc_boundaries` — use OSC B→C region as echo, C→D as output
  - `deterministic_byte_match` — walk the output matching exact bytes sent
    to stdin, skip ConPTY line-wrap CR/LF (cmd's strategy)
  - `none` — don't strip echo (REPLs where echo is cosmetically acceptable)
- **`async_interleave`** — how to handle output produced by background
  concurrency primitives (iex BEAM processes, Python asyncio, Node EventEmitter).
  Default `none`; set to `redraw_detect` for BEAM-family runtimes.

---

## 8. `input` — input delivery

```yaml
input:
  line_ending: "\n"
  multiline_detect: prompt_based | wrapper | balanced_parens | indent_based | none
  multiline_delivery: direct | tempfile | heredoc | wrapper
  multiline_wrapper:
    open: ":{"
    close: ":}"
    trigger: auto | always | never
  balanced_parens:
    open: ['(', '[', '{']
    close: [')', ']', '}']
    string_delims: ['"']
    escape: '\'
    line_comment: ';'
    block_comment: ['#|', '|#']
    char_literal_prefix: '#\'        # reader-macro: #\( is not an open paren
    datum_comment_prefix: '#;'       # reader-macro: #;expr skips next datum
  tempfile:
    prefix: .splash-exec-
    extension: .ps1
    path_template: "{temp_dir}/.splash-exec-{pid}-{guid}.ps1"
    invocation_template: ". '{path}'; Remove-Item '{path}' -ErrorAction SilentlyContinue"
    history_filter: '\.splash-exec-.*\.ps1'
    cleanup_on_start: true
    stale_ttl_hours: 24
  chunk_delay_ms: 0
```

- **`multiline_detect`** — how the runtime decides whether a block of input is
  still incomplete:
  - `prompt_based` — send lines one at a time, watch for continuation prompt
    (Python `... `, bash `> `, iex `...(N)>`)
  - `wrapper` — wrap the block in open/close markers (ghci `:{ ... :}`)
  - `balanced_parens` — count syntactic brackets (Lisp family)
  - `indent_based` — reserved for v2 (Python-style significant indent)
  - `none` — single-line only; multi-line goes via tempfile
- **`multiline_delivery`** — how a confirmed-complete multi-line block
  reaches the interpreter:
  - `direct` — write line-by-line to PTY stdin (bash, zsh, most REPLs)
  - `tempfile` — write the body to a temp file and dot-source it (pwsh's
    `.splash-exec-*.ps1`, cmd's `.splash-exec-*.cmd`)
  - `heredoc` — send `cat <<EOF ... EOF` construct (reserved)
  - `wrapper` — send `wrapper.open + body + wrapper.close` (ghci)
- **`tempfile.history_filter`** — regex matched against shell history entries.
  Lines matching this are hidden from shell history so splash's
  implementation detail doesn't pollute the user's `Up-arrow` recall.
- **`balanced_parens.char_literal_prefix`** — reader-macro prefix that
  escapes a single character from bracket counting (Racket's `#\`,
  Common Lisp's `#\`, Scheme's `#\`). The counter consumes the prefix,
  the following character, and any trailing identifier characters
  (so named literals like `#\space` are handled), and does NOT treat
  embedded brackets inside the literal as syntactic parens.
- **`balanced_parens.datum_comment_prefix`** — reader-macro prefix that
  skips the next balanced datum entirely (Racket / R6RS Scheme `#;`).
  The counter tracks pending datum comments and resolves them when
  the next atom, string, or matching close bracket appears. Multiple
  `#;` may stack (`#;#;(a)(b)` skips two following datums).

---

## 9. `modes` — REPL modes (optional)

```yaml
modes:
  - name: main
    primary: "\u0001SPLASH\u0001iex(?)> "
    default: true
  - name: pry
    auto_enter: true
    detect: '^Break reached:'
    primary: '^pry\(\d+\)> $'
    nested: false
    level_capture: null
    exit_commands:
      - { command: "continue", effect: resume }
      - { command: "respawn()", effect: return_to_toplevel }
    exit_detect: '^\u0001SPLASH\u0001iex\(\d+\)> $'
```

- **`auto_enter: true`** — this mode is entered by the REPL itself (e.g. an
  unhandled exception dropping into a debugger), not by an explicit user
  keystroke. Runtime must re-check mode on every response.
- **`nested: true`** — this mode can stack on itself (SBCL debugger `0] 1] 2]`).
  Requires `level_capture` in the prompt regex.
- **`exit_commands.effect`** — semantic label for what happens when the exit
  command is run:
  - `return_to_toplevel` — unwind all the way to main mode
  - `unwind_one_level` — pop one level of nesting
  - `invoke_restart` — Lisp restart invocation
  - `resume` — continue execution from where the mode was entered

---

## 10. `commands` — helper/meta commands (optional)

```yaml
commands:
  prefix: ":"                # ":" for ghci/SBCL, "" for iex (helpers are plain calls)
  scope: [main, debugger]    # subset of modes where commands are valid
  discovery: ":help"         # command to list all available commands
  builtin:
    - { name: type, syntax: ":type {expr}", description: Show type of expression }
    - { name: load, syntax: ":load {file}", description: Load a source file }
```

This is a hint-only section. The runtime uses it to populate LLM tool
descriptions; it doesn't enforce or parse the commands itself.

---

## 11. `signals`

```yaml
signals:
  interrupt: "\x03"          # Ctrl-C — null if no safe interrupt byte exists
  eof: "\x04"                # Ctrl-D
  suspend: "\x1a"            # Ctrl-Z (null if unsupported)
  interrupt_confirm: null    # optional second keystroke (erl BREAK menu "a")
```

`interrupt` is nullable and must be set to `null` when the adapter's
host has a **destructive** Ctrl-C handler — i.e. one that kills the
entire process instead of unwinding the running command. Setting it
to `null` tells MCP clients not to attempt a `send_input "\x03"` as
a rescue mechanism; their only recovery path is `lifecycle.shutdown`
or waiting for the command to finish. `capabilities.interrupt` must
also be `false` in that case, so adapter consumers have two
consistent signals for the same truth. Example: `groovy` sets
`interrupt: null` because `groovysh`'s Ctrl-C terminates the JVM.

If the host *does* deliver Ctrl-C as a cooperative interrupt but
the delivery is unreliable (e.g. Node's event-loop-bound signal
handler can't fire while a sync JS loop or pending top-level await
blocks the thread), keep `signals.interrupt: "\x03"` and set
`capabilities.interrupt: false`. The split says "the byte exists
but don't count on it".

---

## 12. `lifecycle`

```yaml
lifecycle:
  ready_timeout_ms: 0
  shutdown:
    command: "exit"
    grace_ms: 1000
    force_signal: kill
  restart_on: [crash]        # or [crash, idle_timeout]
```

---

## 13. `capabilities`

Feature flags that the runtime and MCP clients can query.

| Flag | Type | Meaning |
|---|---|---|
| `stateful` | bool | State persists across commands (always true for REPLs and shells) |
| `interrupt` | bool | Ctrl-C can interrupt a running command |
| `meta_commands` | bool | `commands` section is populated and usable |
| `auto_modes` | bool | Adapter has modes with `auto_enter: true` — clients must re-check mode each turn |
| `async_output` | bool | Background concurrency can produce output between commands |
| `exit_code` | `true` \| `false` \| `unreliable` | Exit code fidelity. `unreliable` means always 0 (cmd's limitation) |
| `cwd_tracking` | bool | Adapter emits cwd updates via OSC P (or equivalent) |
| `cwd_format` | `windows_native` \| `posix` \| `none` | Shape of reported cwd strings. `windows_native` (`C:\foo`) can be passed to CreateProcess's `lpCurrentDirectory` directly; `posix` (`/mnt/c/foo`, `/home/u`) forces splash to inject a `cd` preamble at the command level when spawning a replacement console. Only meaningful when `cwd_tracking: true`. |
| `job_control` | bool | `&`, `fg`, `bg`, Ctrl-Z suspend work |
| `shell_integration` | string \| null | Protocol name: `osc633`, `iterm2`, `kitty`, or null |
| `user_busy_detection` | enum | How to detect the user is typing: `osc_b`, `process_polling`, `none` |
| `user_busy_detection_params` | object | Tuning params when method is `process_polling` |

---

## 14. `probe`

Single eval + expected regex. Used as a health check when the adapter is
loaded. If `probe.eval` doesn't match `probe.expect`, the adapter is
considered broken.

```yaml
probe:
  eval: "1 + 1"
  expect: '^2$'
```

---

## 15. `tests` — contract tests

Each test describes a (setup, eval, expect) triple. Run by CI against the
declared interpreter binary. The test vocabulary is intentionally small so
new adapters can be added without learning a test framework.

```yaml
tests:
  - name: simple_arithmetic
    eval: "2 + 3"
    expect: '^5$'
  - name: variable_persistence
    setup: "x = 42"
    eval: "x * 2"
    expect: '^84$'
  - name: error_recovery
    eval: "1/0"
    expect_error: true
    expect: 'ZeroDivisionError'
  - name: cwd_change_tracked
    setup: "cd /tmp"
    eval: "pwd"
    expect_cwd_update: true
  - name: exit_code_propagates
    eval: "false"
    expect_exit_code: 1
  - name: nested_sequence
    setup_sequence:
      - { eval: "(/ 1 0)", expect_mode: debugger, expect_level: 0 }
      - { eval: ":abort", expect_mode: toplevel }
```

Supported assertions:
- `expect: <regex>` — stdout must match
- `expect_error: bool` — evaluation must fail (stderr/exception)
- `expect_exit_code: int` — for shells only
- `expect_cwd_update: bool` — cwd must have been emitted since last prompt
- `expect_mode: <name>` — after eval, the REPL must be in this mode
- `expect_level: int` — nested mode depth
- `expect_out_of_band: <regex>` — async-emitted text must match
- `exit_code_is_unreliable: true` — tag this test as documenting a known
  limitation rather than a correctness invariant

---

## 16. Writing a new adapter

1. Copy the closest existing adapter as a template.
2. Replace `name`, `version`, `description`, `family`.
3. Adjust `process.command_template` to launch your interpreter.
4. Decide on `init.strategy`:
   - Shell with OSC 633 integration → `shell_integration`
   - REPL where PS1 can be replaced → `marker`
   - REPL where PS1 is hardcoded → `regex`
5. Write the integration script (if any) with the unique marker injection.
6. Fill in `tests:` — aim for at least 5, covering simple eval, state
   persistence, multi-line input, error recovery, and any mode transitions.
7. Run `splash adapter test adapters/your-adapter.yaml` to verify it.
8. Submit a PR to the adapter registry.

---

## 17. Versioning policy

- **`schema` field is sacred.** Once `schema: 1` ships on a stable splash
  release, additions are allowed; removals and semantic changes require
  `schema: 2`.
- **Adapter `version` field is independent** of the schema version. Adapters
  can bump their own version when the integration script is tweaked or new
  tests are added.
- **Interpreter version compatibility** — adapters should document supported
  interpreter version ranges in the `description` field if compatibility is
  narrow. The runtime does not enforce this.

---

## 18. Open questions for v1 freeze

- [x] **Q1: Is `balanced_parens` expressive enough for Lisp-family
  languages with reader macros?** — Answered "yes, with the
  char_literal_prefix + datum_comment_prefix extensions" by the
  Racket adapter + runtime counter (0.1.0 → 0.2.0, 2026-04-14).
  The counter (`Services/BalancedParensCounter.cs`) is a single
  forward pass that tracks bracket depth, string-literal state,
  line/block comment state, and the two reader-macro extensions
  added to close Q1:
    - **`char_literal_prefix`** consumes the prefix + the next
      character (plus any identifier run for named literals like
      `#\space`), so `#\(` is treated as a literal char token
      rather than an unclosed open paren.
    - **`datum_comment_prefix`** pushes a pending-comment marker
      that is resolved when the next atom / string / matching
      close bracket appears, so `#;(long list)` balances its
      own brackets without affecting outer depth. Multiple `#;`
      stack (`#;#;(a)(b)` skips two datums) via a counter.
  The counter ships with 26 unit tests covering all the Lisp
  edge cases (char literals of every bracket type, datum
  comments on atoms / strings / lists, nested datum comments,
  strings with embedded brackets, unterminated literals). The
  Racket adapter's `multiline_detect: balanced_parens` is now
  wired into `ConsoleWorker`'s execute path: an AI-sent block
  that fails the counter (`(define (f x)`, `(+ 1 2))`, etc.) is
  rejected with a clear diagnostic rather than being submitted
  to the REPL where it would deadlock. The quoting prefixes
  `'` / `` ` `` / `,@` don't need schema support because they
  don't affect bracket counting — they just annotate the next
  datum, which the counter already handles.
- [x] **Q2: Should `modes.exit_commands.effect` enum stay closed (4
  values) or allow `custom` with a free-text label?** — Answered
  "closed is sufficient" by the python adapter's pdb mode declaration
  + the runtime `ModeDetector` (0.2.0, 2026-04-14). pdb's exit
  commands (`continue`/`c` = resume, `quit`/`q` = return to Python
  REPL) map cleanly to `resume` and `return_to_toplevel`; no need
  for `custom` or a free-text label. The `invoke_restart` and
  `unwind_one_level` values still lack a live example but are kept
  for CL/SBCL-style debuggers where they have prior art (SLIME's
  restart protocol). Add a new enum value only when a concrete
  adapter demands one. Runtime status: `ConsoleWorker` now walks
  the mode graph after every command via `ModeDetector` (a pure
  forward regex pass over the captured output tail), surfaces
  `currentMode` / `currentModeLevel` on execute and get_status
  responses, and `AdapterDeclaredTestsRunner` honours `expect_mode`
  / `expect_level` assertions. Exit-command enforcement is still
  client-side (the AI / MCP client picks an exit_command and the
  detector confirms the post-command mode), which is the right
  layering — the runtime reports what mode it sees, the client
  decides whether to issue the exit command.
- [ ] Is `output.async_interleave.strategy: redraw_detect` sufficient for
  asyncio / Go-like coroutines, or do we need per-family variants?
- [ ] Should adapters be able to bundle `preset:` references (e.g.
  `balanced_parens: { preset: lisp }`) to reduce duplication across Lisp
  family adapters?

These should be resolved before stamping `schema: 1` on a shipped release.
