# Gotchas

Tripwires that still bite, grouped by what you're likely to be
touching when you hit them. Historical "we fixed this in commit X
and it hasn't come back" entries are not here — for those, consult
`git log`. If something here stops being a live risk, delete it;
don't turn this into an archive.

## Build / packaging

- **YamlDotNet static generator** is the package
  `Vecc.YamlDotNet.Analyzers.StaticGenerator`, NOT
  `YamlDotNet.Analyzers.StaticGenerator` (the latter doesn't exist).
  Every nested type needs `[YamlSerializable]` in
  `AdapterStaticContext.cs` — the generator does not walk properties.

## Registry lookup

- **`NormalizeShellFamily` stays.** It looks like a hardcoded shell-
  family helper but it's the path-to-registry-key normaliser that
  `AdapterRegistry.Find` itself uses as a lookup key —
  `Path.GetFileNameWithoutExtension(shell).ToLowerInvariant()`. The
  last *real* shell-family literal in `ConsoleManager`
  (`IsWindowsNativeShellFamily`) was replaced by `cwd_format` a long
  time ago. Routing-layer invariants (shell-mandatory, per-shell MRU
  stack) are documented in `docs/ARCHITECTURE.md`.

## Worker / PTY / cache layer

- **`CancellationTokenRegistration.Dispose` blocks from inside the
  callback.** When a CTS-registered delegate calls back into code
  that disposes its own registration, `Dispose` waits for the
  callback to finish — which is the same thread currently inside
  the callback. Instant deadlock. First hit in `FlipToCacheMode`
  wiring; fix is to leave disposal to `Resolve`'s cache branch and
  `AbortPending`, where the command has finished running and there's
  no active callback to wait for.
- **ModeDetector input source: raw ring bytes, not the OSC slice or
  VtLite snapshot.** The mode transition signal lives in the NEXT
  prompt (`1 > ` / `(Pdb) ` / `N] `), which arrives AFTER OSC A
  fires Resolve — so `cleanedOutput` (the OSC-C..D slice) can never
  contain it, and every mode transition silently falls through to
  the default. Worse, `GetRecentOutputSnapshot()` runs the ring
  through VtLite which reshapes the trailing prompt into cell-
  addressed coordinates that break `^<prompt>$` anchored regexes.
  `HandleExecuteAsync` scans `_tracker.GetRawRecentBytes()` in a
  short 150 ms poll loop, breaking out as soon as a non-default
  auto_enter mode matches.
- **OSC title sequences split across PTY read chunks.** The read
  loop's chunk size is ~4 KB, and a shell's PROMPT_COMMAND that
  writes `\e]0;long title\a` can easily land half in one chunk and
  half in the next. `ReplaceOscTitle(..., ref string pendingTail)`
  carries the unterminated opener to the next call via
  `_oscTitlePending` on the worker.
- **C# `\x07` is greedy.** `\x07after` parses as `\u07AF` + `ter`
  (four hex digits eaten), not BEL + `after`. Test strings that
  embed BEL terminators need to use `\a` instead — a single-
  character escape for `\u0007` that can't over-match. Applies to
  `\x0e` (→ `\u0EAF`) and any other pairing where the literal
  continuation is a hex digit; prefer named escapes (`\a`, `\b`,
  `\t`, `\r`, `\n`) or explicit `\u0007` when writing test fixtures.

## Windows-specific spawning

- **`SW_SHOWNOACTIVATE` on `CreateProcessW`.** Spawning a worker
  with `CREATE_NEW_CONSOLE` alone makes Windows move keyboard focus
  to the new console by default — if the user is typing in their
  editor when a console launches, their keystrokes land in ripple's
  shell until they notice and re-focus, and those buffered bytes
  get prepended to the next AI command. `STARTF_USESHOWWINDOW +
  SW_SHOWNOACTIVATE` in `STARTUPINFOW` shows the window without
  activating it.
- **AppLocker blocks spawns from `%USERPROFILE%`** on locked-down
  corporate boxes, surfacing only when ConPTY attaches the
  pseudoconsole (error 5 = ACCESS_DENIED). `Process.Start` from the
  same user-dir path works fine, so it's specifically the ripple
  `--console`-mode ConPTY spawn being filtered. Binaries in
  `C:\Program Files\**` are whitelisted. Affects cold-start CCL
  (user-dir install), homebrew Racket (Program Files is fine),
  anything installed to `%LOCALAPPDATA%` without IT whitelisting.

## Shell integration

- **WSL bash needs an installed distribution.**
  `C:\Windows\System32\bash.exe` is the WSL launcher; on a box where
  `wsl -l -v` says "has no installed distributions", the launcher
  exits ~150 ms after `CreateProcessW`. `bash.yaml` declares
  `process.executable_candidates` with Git for Windows' bundled
  `C:\Program Files\Git\bin\bash.exe` first, so the worker picks
  Git Bash (or MSYS2 if Git isn't installed) before falling through
  to WSL.
- **MSYS2 zsh under ConPTY deadlocks the pty_inject flow.** ZLE
  buffers PTY-written bytes but treats neither `\n` nor `\r\n` as a
  reliable Enter from the worker's `WriteToPty`, so the `source
  '<tmpfile>'; rm -f '<tmpfile>'` injection never submits.
  `zsh.yaml` uses `init.delivery: rc_file` with `dir_env_var:
  ZDOTDIR`: the worker stages `integration.zsh` as
  `<ZDOTDIR>/.zshrc` before `CreateProcessW` and exports `ZDOTDIR`
  into the child env. zsh sources `.zshrc` as part of its own
  startup, so `precmd` / `preexec` hooks are live by the first
  prompt.
- **`multiline_delivery` is schema-partial.** The generic tempfile-
  dispatch path (`MultilineDelivery == "tempfile"` +
  `tempfile.invocation_template`) works for REPL adapters that
  declare it (python, node, racket, ccl, abcl). pwsh / cmd / bash /
  zsh get tempfile behaviour via *hardcoded* `isMultiLinePwsh` /
  `isMultiLineCmd` / `isMultiLinePosix` branches in
  `HandleExecuteAsync` that take precedence over the schema
  dispatch. pwsh's hardcoded path is genuinely more complex than the
  schema can express today (cursor-wrap-aware echo via
  `BuildMultiLineTempfileBody`); generalising it needs new schema
  surface.

## REPL quirks

- **ConPTY + OSC 633 on Node.js.** Putting OSC bytes inside the
  prompt STRING breaks cursor math because readline's
  `getStringWidth` strips the escapes but ConPTY advances its
  tracked cursor for every unrecognised byte. Fix: write OSC out-of-
  band via `process.stdout.write` *after* the original
  `displayPrompt`.
- **Python 3.13 pyrepl** calls `str(sys.ps1)` per keystroke, which
  would flood OSC emission. `python.yaml` sets
  `process.env.PYTHON_BASIC_REPL=1` to force the old parser-based
  REPL where `sys.ps1` is evaluated exactly once per prompt.
- **Python basic REPL has no line editor.** `PYTHON_BASIC_REPL=1`
  disables readline, so `input.clear_line: "\x01\x0b"` produces
  `SyntaxError: invalid non-printable character U+0001` — python
  parses the Ctrl-A byte as literal source code. Same for fsi with
  `--readline-`, racket with `-i`, CCL, and ABCL — none have a
  line editor the clear bytes can target. `clear_line` defaults to
  `null`; only set for bash/zsh where empirical verification
  confirmed the readline/ZLE emacs default handles the bytes as
  no-ops on an empty buffer.
- **pdb's `(Pdb) ` prompt bypasses `sys.ps1`.** Python's pdb runs
  its own read-eval loop instead of going through the main REPL's
  input path, so the integration script's OSC-emitting prompt hook
  never fires for `(Pdb) `. An `execute_command` that triggers
  `pdb.set_trace()` hangs waiting for an OSC A that never comes,
  then returns `timedOut=true`. Closing this would require a pdb-
  aware hook via `sys.monitoring` or a `pdb.Pdb` subclass —
  deferred.
- **Racket's `-i` REPL does NOT inherit `current-prompt-read`** set
  during `-f` loading. Parameters are thread-local and the
  interactive loop runs inside a fresh continuation barrier, so a
  naive `racket -i -f integration.rkt` silently reverts to the
  default `> ` prompt. The integration script calls
  `(read-eval-print-loop)` itself after wiring up the parameter —
  we drive the REPL from our own code rather than from the binary's
  built-in `-i` path.
- **CCL hard-locks `ccl::print-listener-prompt` redefinition** by
  default. Binding `ccl:*warn-if-redefine-kernel*` to `nil`
  downgrades the check to a no-op so
  `(setf (symbol-function 'ccl::print-listener-prompt) ...)` takes
  effect.
- **F# Interactive (`dotnet fsi`) requires 4 settings** to work
  under ConPTY:
  1. `--gui-` is mandatory. Default `--gui+` runs interactions on a
     Windows Forms event loop that silently fails under ConPTY.
  2. `--readline-` strongly recommended. Without it, fsi rewrites
     the prompt line via cursor positioning on every keystroke.
  3. `--use:<file>` with any script (even an empty one) is required
     to keep fsi alive. Plain `dotnet fsi` under ConPTY exits within
     ~80ms before the first prompt — dotnet-host TTY-detection edge
     case.
  4. `ready.delay_after_inject_ms: 800`. fsi prints the post-
     script-load prompt ~200ms before the eval loop wires up stdin.
- **ABCL 1.9.2 + JDK 21 virtual-threading warning.** ABCL prints a
  red "Failed to introspect virtual threading methods" warning
  before the banner on cold start — ABCL compiled against older JDK
  module access, JDK 21 tightened it. The warning pollutes the pre-
  banner output and confuses the ready-phase settle. `abcl.yaml`
  passes `--add-opens java.base/java.lang=ALL-UNNAMED` to
  `java.exe` to silence it.
- **ABCL default prompt is `CL-USER(N): `, not `? `.** The ripple
  integration overrides `top-level::*repl-prompt-fun*` to emit
  literal `? ` instead so both Common Lisp adapters (CCL and ABCL)
  share the same mode regex `^\? $`. If a future tweak restores
  ABCL's native prompt, the mode regex needs alternation.
- **ABCL's debugger is `system::debug-loop`, different from the
  top-level prompt mechanism.** The CCL-style `N > ` break-loop
  prompt + `level_capture` mode regex don't port to ABCL — the
  debug-loop prints a separate prompt that the top-level hook
  doesn't see. ABCL ships with only `main` mode; debugger mode is
  deferred.
- **Node REPL single-threaded signal handling.** Node's event loop
  runs user JS and the Ctrl-C signal handler on the same thread. A
  sync `while (true) { ... }` blocks the event loop, so the signal
  handler never fires — sending `\x03` is a no-op. Top-level
  `await` has the same shape. `node.yaml` sets
  `capabilities.interrupt: false` even though `signals.interrupt:
  "\x03"` is set: the byte exists for the rare case Node yields at
  a cooperative point, but don't rely on it.
- **groovysh Ctrl-C is destructive.** Sending `\x03` terminates the
  JVM and closes the shell, rather than unwinding back to the
  prompt. No groovysh flag downgrades this. `groovy.yaml` declares
  `signals.interrupt: null` and `capabilities.interrupt: false`;
  only safe recovery is `:quit` or waiting.
- **duckdb 1.5 prompt is `<dbname> D `, not bare `D `.** 1.5
  switched to a shell-style `<dbname> D ` (dbname is `memory` for
  `:memory:`, filename-basename after `.open`). `duckdb.yaml` uses
  `^\S+ D $` as the prompt regex; `BuildPromptRedrawMatcher` strips
  the `^`/`$` anchors and `\G`-anchors at scan position so the same
  regex consumes the redrawn prompt prefix under `fuzzy_byte_match`.
- **PostgreSQL for psql adapter tests** needs libpq env vars in
  the user-scope registry. The ripple worker launches with a clean
  environment block (`LaunchWithCleanEnvironment` in
  `ProcessLauncher.cs`, `bInherit=false` in
  `CreateEnvironmentBlock`), so `$env:PGPORT` set in the calling
  pwsh session never reaches psql — it has to be in the registry
  user-scope. Set via `[Environment]::SetEnvironmentVariable('PGPORT',
  '55432','User')` etc. for `PGHOST` / `PGUSER` / `PGDATABASE` /
  `PGPASSWORD`.
