# Session handoff — start here

Entry point for any future Claude Code (or human) session walking into
this repo cold. Read this first, then follow the reading list below
for whatever depth you need. Updated after the cache-on-busy-receive
salvage round (commit `af4e5e5`) and the ABCL adapter round
(commit `cd1cef4`) on 2026-04-15.

---

## Current state (1 paragraph)

**splash** is a declarative adapter framework that exposes any
interactive process (shells, REPLs, eventually debuggers) to an AI
via MCP over ConPTY/forkpty. Phase B (YAML-drive the existing shell
runtime), phase C (framework generalisation), the phase C+ punch
list (Racket adapter, pdb mode declaration, `--probe-adapters` CLI,
`ready.output_settled_*` timing knobs, BOM fix, `--list-adapters`
summary truncation, CA1416 cleanup, runtime `balanced_parens`
counter, runtime `modes` graph walker), the regex-strategy round
(CSI-aware `RegexPromptDetector`, `process.executable` override,
**F# Interactive** and **Java jshell** adapters), the
**cache-on-busy-receive salvage layer** (multi-entry per-console
cache list, `FlipToCacheMode`, 170 s preemptive timeout, baked
status lines, universal drain wrapper on every MCP tool), and the
**ABCL adapter round** (JVM-hosted Common Lisp via the Groovy
pattern — `java -jar abcl.jar` from `%LOCALAPPDATA%\splash-deps\`,
second CL evidence point for the `balanced_parens` counter) are
all complete. **11 adapters ship embedded** (pwsh, bash, zsh, cmd,
python, node, racket, **abcl**, fsi, jshell, groovy; plus `ccl`
which ships in the source tree but is gitignored on this box —
originally because corporate AppLocker was blocking user-dir PE
files under ConPTY, though the ccl tests pass on this box as of
2026-04-15 so the policy may have been relaxed; `ccl` stays
locally gitignored until we confirm that and decide to ship it
embedded). **536 assertions** pass on `--test --e2e` (408 unit +
79 pre-existing E2E + 49 adapter-declared, +6 from the ABCL round).
Zero
shell-family literals survive in the C# runtime outside the
registry key normaliser. Schema §18 Q1 (balanced_parens vs reader
macros) is **closed** by the runtime counter and `char_literal_prefix`
/ `datum_comment_prefix` schema extensions; §18 Q2
(exit_commands.effect enum) is **closed** by the python adapter's
pdb mode + the runtime `ModeDetector` and `expect_mode` test-runner
support. Q3 and Q4 remain untouched — both are blocked on a
BEAM/Go-style adapter, not on splash itself. **The schema is ready
to freeze as `v1 stable`** the next time someone is willing to
stamp it; no remaining runtime gates.

---

## Warm-up checklist (30 seconds)

```powershell
cd C:\MyProj\splash
git log --oneline -35                                     # last 35 commits — phase B/C/C+ + regex-strategy + cache-on-busy-receive + ABCL
./bin/Debug/net9.0/splash.exe --list-adapters             # 11 adapters (12 on this box if ccl embedded)
./bin/Debug/net9.0/splash.exe --probe-adapters            # opt-in pre-flight, one probe.eval per adapter
./bin/Debug/net9.0/splash.exe --test --e2e                # 536 / 536 green, zsh SKIP expected
./bin/Debug/net9.0/splash.exe --adapter-tests --only abcl # isolated declared-test run for a single adapter
```

If the Debug binary is missing or stale:

```powershell
dotnet build -c Debug                              # fast local build
./Build.ps1                                        # AOT Release → dist/splash.exe (slower)
```

`./Build.ps1` is what splash-dev (via mcp-sitter) picks up for
hot-reload cycles during the session. If you use the `splash-dev`
MCP server, remember the flow: `sitter_kill` to unlock the binary,
`./Build.ps1` via another MCP (pwsh) to rebuild, then any
`splash-dev` tool call triggers lazy respawn of the fresh child.

---

## Reading list (prioritised)

### Always-on

| Doc | Why |
|---|---|
| **This file** (`HANDOFF.md`) | State of play, what to read next, gotchas |
| **[`adapters/SCHEMA.md`](adapters/SCHEMA.md)** | Normative schema contract. §18 lists the open questions waiting for v1 freeze |
| **Git log** (`git log --oneline -40`) | The phase B/C arc is narrated in commit messages — each one is a self-contained story |

### Reference (read when you need depth)

| Area | Files |
|---|---|
| Schema types in C# | `Services/Adapters/AdapterModel.cs`, `AdapterStaticContext.cs` (AOT-safe YamlDotNet) |
| Loader & registry | `Services/Adapters/AdapterLoader.cs`, `AdapterRegistry.cs` (embedded + `~/.splash/adapters/` merge with override semantics) |
| Worker launch / exec | `Services/ConsoleWorker.cs` — `BuildCommandLine` and `RunAsync`'s ready phase are the adapter-driven hotspots |
| Proxy (MCP-facing) | `Services/ConsoleManager.cs` — the last shell-family literal (`NormalizeShellFamily`) is a registry key normaliser, not a family check |
| ConPTY + env merge | `Services/ConPty.cs` — unified env block builder applies `adapter.process.env` to both inherit-env and clean-env paths |
| Integration scripts | `ShellIntegration/*.{ps1,bash,zsh,py,js,rkt,fsx,abcl.lisp}` — the single source of truth for each shell's OSC 633 emitter. fsi's integration.fsx is intentionally empty (just comments) because F# Interactive has no prompt-replacement API; see Gotchas below. The two Common-Lisp integrations (`integration.lisp` for CCL, `integration.abcl.lisp` for ABCL) differ only in which prompt hook they override — CCL uses `(setf (symbol-function 'ccl::print-listener-prompt) ...)` behind a kernel-redefine warning gate, ABCL uses a simple `(setf top-level::*repl-prompt-fun* ...)`. |
| Adapter YAMLs | `adapters/*.yaml` — 11 live examples embedded in the binary covering every schema section that's currently consumed. The `groovy` / `abcl` pair documents the **Groovy pattern**: invoke `java.exe` from Program Files with `-jar %LOCALAPPDATA%\splash-deps\<lang>\payload.jar` so the only spawned executable sits in a whitelisted path while the jar payload loads as regular classfiles from user-dir. Any future JVM-hosted REPL (Clojure, Kotlin REPL, JRuby, Jython, Scala) picks this up for free by cloning `abcl.yaml` and swapping the jar path + prompt regex / hook. |
| Regex prompt detector | `Services/RegexPromptDetector.cs` — CSI-aware, strips ANSI escapes internally and substitutes cursor-to-col-1 positioning with `\n` so adapter authors can write natural `^<prompt>$` patterns. Used by fsi and jshell; future ConPTY-rendering REPLs (ghci, bb, etc.) inherit this for free. |
| Cache / drain layer | `Services/CommandTracker.cs` (`_cachedResults` list, `FlipToCacheMode`, `ConsumeCachedOutputs`, `BuildStatusLine`, `PreemptiveTimeoutMs`), `Services/ConsoleWorker.cs` (`HandleExecuteAsync` busy-flip path, `HandleGetCachedOutput` array serialisation), `Services/ConsoleManager.cs` (`CollectCachedOutputsAsync` / `WaitForCompletionAsync` array consumers, `MaxExecuteTimeoutSeconds`), `Tools/ShellTools.cs` (`AppendCachedOutputs` universal wrapper that every MCP tool funnels through). |
| Declarative test runner | `Tests/AdapterDeclaredTestsRunner.cs` — how each adapter's `tests:` block becomes a live worker assertion |
| Existing E2E plumbing | `Tests/ConsoleWorkerTests.cs` — `WaitForPipeAsync` / `SendRequest` are `internal` for runner reuse |

### Supplemental (optional, session-local)

`scratch/phase-b-handoff.md` — a longer prose version of the B/C
arc, including per-commit explanations and more gotchas. **Only
available locally** (the `scratch/` directory is in `.gitignore`),
so treat it as working-tree notes that may or may not exist on any
given machine.

---

## Architecture in 5 bullets

1. **Proxy / worker split.** `splash.exe` in MCP mode is the proxy
   that serves AI tool calls. When the AI asks to open a shell, the
   proxy launches `splash.exe --console <shell>` in a new window —
   that's the worker, which owns the ConPTY pseudoconsole and PTY I/O.
   Proxy and worker talk over a Named Pipe (`SP.{proxyPid}.{agent}.{consolePid}`)
   using a framed JSON RPC.

2. **Adapter-driven launch.** `ConsoleWorker.BuildCommandLine` reads
   `adapter.process.command_template` and expands `{shell_path}`,
   `{init_invocation}`, `{prompt_template}`, `{tempfile_path}` into
   the final `CreateProcessW` command line. The worker also reads
   `adapter.process.env` (merged into the Win32 environment block
   by `ConPty.cs` regardless of inherit vs clean), `adapter.ready.*`
   for the startup synchronisation flow (including the tunable
   `output_settled_{min,stable,max}_ms` knobs on `WaitForOutputSettled`),
   `adapter.init.*` for the integration script delivery,
   `adapter.input.line_ending` for how Enter is written to the PTY,
   `adapter.output.input_echo_strategy` for how input echo is
   stripped from captured output, and
   `adapter.capabilities.{user_busy_detection,cwd_format,...}` for
   the feature flags that change runtime behaviour.

3. **OSC 633 is the tracker's primary language; `prompt.strategy: regex`
   is the escape hatch.** Shell-integration adapters emit OSC 633
   sequences (A = prompt start, B = input start, C = command
   executing, D;N = command finished with exit code N, P;Cwd=... =
   cwd update) from their integration script. The worker parses
   these via `OscParser`, `CommandTracker` slices the output buffer
   between C and D, and the MCP response carries output + exit code
   + cwd back to the AI. REPL adapters (python, node, racket, ccl)
   install hooks that emit the same OSC events — `sys.ps1.__str__`
   for Python, `displayPrompt` + out-of-band `process.stdout.write`
   for Node, `current-prompt-read` override for Racket, the locked
   `ccl::print-listener-prompt` override for CCL. Adapters whose
   host has NO prompt-replacement API (fsi, jshell) declare
   `prompt.strategy: regex` and let `RegexPromptDetector` synthesize
   the equivalent PromptStart / CommandFinished events from a regex
   match against the visible text. The detector is CSI-aware: it
   strips cursor positioning and color escapes before matching and
   translates match positions back to original-byte coordinates so
   the downstream tracker sees one coherent event stream regardless
   of which strategy fired. The tracker has exactly one code path
   for all three cases.

4. **External adapters override embedded ones.** At startup,
   `AdapterRegistry.LoadDefault()` merges `Splash.adapters.*.yaml`
   (embedded resources, baked into the binary at build time) with
   any `*.yaml` dropped into `~/.splash/adapters/`. External adapters
   override embedded ones of the same name, with the override logged
   in the startup report so you can see it in `splash --list-adapters`.
   The external path resolves `script_resource` relative to the
   YAML's directory first, then falls back to the embedded
   `ShellIntegration/*` resources.

5. **`splash --test --e2e` is the contract gate.** It runs unit
   tests, the pre-existing pipe-protocol E2E suite, the multi-shell
   cross-verification, then finally walks every loaded adapter's
   `tests:` block via `AdapterDeclaredTestsRunner`. Missing
   interpreters (e.g. no zsh on this Windows box) are soft-skipped
   so CI stays green on partial toolchains. An adapter's `probe`
   runs as a synthetic first test — a broken adapter fails fast
   instead of flooding the output with downstream failures. The
   same probe loop is reachable standalone via
   `splash --probe-adapters` (opt-in, no other tests).

6. **Cache / drain salvage layer.** The MCP client can silently drop
   an in-flight response (user hits ESC; a new tool call lands while
   the previous one is still running; the protocol's own 3-minute
   ceiling). splash can't detect client-side cancel directly, so
   instead it flips the in-flight command to **cache-on-complete
   mode** whenever any of those signals fire: the 170 s preemptive
   timer in `CommandTracker.RegisterCommand` (hard-capped under the
   MCP ceiling), or `ConsoleWorker.HandleExecuteAsync` catching a
   fresh execute on a busy console. The flipped command's TCS is
   detached with a `TimeoutException` so `HandleExecuteAsync` can
   return a usable "cached for next tool call" response immediately;
   when the shell eventually finishes and `Resolve` fires, the real
   result is appended to `_cachedResults` — a **list**, because
   sequential flips can stack on a single console. Every subsequent
   MCP tool call routes through `ShellTools.AppendCachedOutputs`,
   which calls `ConsoleManager.CollectCachedOutputsAsync` to
   enumerate the agent's consoles, drains each one's list
   atomically, and renders the `StatusLine` the worker baked at
   `Resolve` time alongside the command output. This means **every**
   MCP tool response — not just execute_command or wait_for_completion
   — surfaces any salvaged results, and the AI sees them without
   having to know what flipped or when.

---

## Next-session candidate work

All runtime gates for v1 freeze are now clear. The remaining
candidates are extensions and external-dependency work:

1. **Stamp `schema: 1 stable`** — purely a docs change. Update
   `adapters/SCHEMA.md` line 7 (`Status: **draft**.`) to
   `Status: **stable** (frozen 2026-XX-XX)` and bump the version
   note. Q1 and Q2 are both closed at the runtime layer; Q3 and
   Q4 are blocked on adapters splash doesn't ship yet, not on
   schema gaps. User opted to defer this until there is a concrete
   reason to stamp it (2026-04-14: "まだやるメリットがない").

2. **Runtime `multiline_detect` gate** — the schema field has
   been declared by three adapters (racket: `balanced_parens`,
   fsi: `none`, jshell: `prompt_based`) but `ConsoleWorker` still
   does not consume it on the input path; `balanced_parens` only
   runs as a validator inside an adapter test helper, and
   `prompt_based` has no runtime meaning yet. Wiring it for real
   would let splash reject syntactically incomplete AI input
   before submitting it to the REPL — avoiding the
   deadlock-on-unbalanced-paren failure mode for Lisp and the
   deadlock-on-unclosed-brace failure mode for Java. `racket.yaml`
   and `jshell.yaml` already describe the intent, so this is
   mostly plumbing + a `prompt_based` detector that watches for
   the declared continuation prompt.

3. **`ready.delay_after_inject_ms` semantics cleanup** — field
   was originally "wait N ms after PTY-injecting the integration
   script before declaring ready" (used by bash/zsh). The fsi
   adapter repurposed it to mean "wait N ms after the first
   regex prompt match before declaring ready" because the same
   pipeline stage is responsible for both, and no adapter needs
   both semantics simultaneously. A future schema cleanup could
   split this into `ready.delay_after_inject_ms` +
   `ready.delay_after_first_prompt_ms`, or rename the existing
   field to something strategy-neutral. Deferred — the double
   semantics is documented in `ConsoleWorker.RunAsync` and the
   fsi adapter comment, and no adapter today depends on the
   distinction.

4. **More reader-macro-heavy Lisp / Haskell adapters** — the Q1
   counter already has two evidence points (Racket and the ABCL
   adapter that shipped in commit `cd1cef4`), plus a locally
   validated third (CCL on this box). A Q4 `balanced_parens:
   { preset: lisp }` could now factor the CL-reader-macro
   specification shared by CCL and ABCL into a registry preset,
   and adding **GHCi** would stress the counter against a different
   reader-macro shape (`{- -}` block comments, `` `backtick` ``
   sections) on the Haskell side. GHCi is still blocked as a
   native PE from `%USERPROFILE%` under AppLocker — the user would
   need to install GHC via `stack`/`ghcup` into a whitelisted path
   like `C:\tools\ghcup\bin` before splash can spawn it via ConPTY.
   Alternatively, **Clojure** runs on the JVM and would ship via
   the Groovy pattern (clone `abcl.yaml`, swap jar + prompt
   regex) without needing a native binary. See also the note on
   CCL below — the policy may have relaxed and CCL could now be
   shipped embedded alongside ABCL.

4b. **Confirm CCL policy status and decide on embedding.** Until
    2026-04-14, the comment was "CCL binary blocked by corporate
    AppLocker under ConPTY `CreateProcessW`". As of 2026-04-15,
    `--adapter-tests` on this box runs CCL's probe + 5 declared
    tests green, which suggests the user-dir PE block has been
    relaxed (either a policy change or an unblock request was
    approved). `adapters/ccl.yaml` and `ShellIntegration/integration.lisp`
    still live under `.git/info/exclude` locally so the committed
    history stays reproducible on boxes that haven't had the
    block lifted. Next step: verify on another box with the old
    policy whether CCL still fails there — if the block is gone
    universally, un-gitignore and commit; otherwise, keep the
    gitignore and add a `CCL (local-only)` note to README. ABCL
    remains the portable default regardless, because it spawns
    only `java.exe` (whitelisted Program Files path) and loads
    abcl.jar as classfiles.

5. **Async-output handling (§18 Q3)** — `redraw_detect` is the
   only defined strategy for `output.async_interleave.strategy`
   and no in-tree adapter exercises it. A future BEAM (iex /
   erlang shell) or Go REPL adapter would surface whether a
   single strategy covers both async families or if per-family
   variants are needed. Blocked on adding one of those adapters.

6. **`balanced_parens: { preset: lisp }` (§18 Q4)** — once a
   second Lisp adapter ships and duplicates Racket's
   `balanced_parens` block almost verbatim, factor the common
   bits into a registry preset an adapter can reference by name.
   Cosmetic / DRY improvement, not a runtime change. Blocked on
   item 4.

7. **Mode exit-command enforcement** — currently `ModeDetector`
   reports the post-command mode, and the MCP client decides
   whether to send an exit command. A stricter model would have
   the runtime check `mode.exit_commands` against the AI-supplied
   command and short-circuit if the AI tries to issue an exit
   command in the wrong mode. Not blocking v1 freeze (the current
   layering is defensible) but would catch a class of AI mistakes
   the same way `balanced_parens` does for incomplete input.

8. **`.gitattributes` renormalisation** — still held off. `git
   add --renormalize .` would touch every tracked file and
   pollute blame history. Do this only if there's a separate
   reason to burn a blame entry. Not worth tackling in isolation.

9. **Pre-existing E2E flakes** — two `ConsoleWorkerTests` E2E
   assertions have been failing intermittently for at least the
   cache/drain round and were confirmed via `git stash` to
   predate it:
   - `Shell returned to standby after Ctrl+C interrupt` — the
     post-Ctrl+C drain-and-poll loop times out waiting for the
     worker to transition back to `standby` within 5 s. The
     shell does interrupt (earlier assertions pass) but something
     in the OSC A / cleanup path after interrupt doesn't settle
     fast enough on this box. Investigate with `splash --console
     pwsh.exe` manually + send_input `\x03` via another proxy.
   - `PTY still alive after obsolete state` — after sending a
     claim from a fake-higher proxy version, the worker marks
     itself obsolete and writes a banner, but the assertion that
     the PTY is still draining bytes fails on this run. The
     visible terminal *is* still alive (manual check), so this
     may be a probe-timing issue in the test harness rather than
     a real regression.
   Both flakes are pre-existing; the cache/drain round did not
   introduce them. They cap `--test --e2e` at 528 / 530 in practice
   — 530 is the count when both pass, 528 the floor when both
   fail.

User policy as of 2026-04-14: **schema is ready to freeze** but
the user opted not to stamp it until there's a concrete reason.
All four §18 questions are either closed (Q1, Q2) or blocked on
external adapters that splash doesn't yet ship (Q3, Q4) — neither
case is a schema gap.

---

## Gotchas (compact)

- **YamlDotNet static generator** is the package
  `Vecc.YamlDotNet.Analyzers.StaticGenerator`, NOT
  `YamlDotNet.Analyzers.StaticGenerator` (the latter doesn't exist).
  Every nested type needs `[YamlSerializable]` in
  `AdapterStaticContext.cs` — the generator does not walk properties.
- **AOT publish from Git Bash** fails with
  `vswhere.exe is not recognized` because vswhere isn't on PATH.
  Prefix with `PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"`
  or run from a Developer PowerShell.
- **ConPTY + OSC 633 on Node.js**: putting OSC bytes inside the
  prompt STRING breaks cursor math because readline's
  `getStringWidth` strips the escapes but ConPTY advances its
  tracked cursor for every unrecognised byte. Fix: write OSC
  out-of-band via `process.stdout.write` *after* the original
  `displayPrompt`. Python escapes this trap because its
  `sys.ps1.__str__` mechanism returns the prompt string wholesale,
  and Python re-evaluates per render.
- **Python 3.13 pyrepl** calls `str(sys.ps1)` per keystroke, which
  would flood OSC emission. `python.yaml` sets
  `process.env.PYTHON_BASIC_REPL=1` to force the old parser-based
  REPL where `sys.ps1` is evaluated exactly once per prompt.
- **Racket's `-i` REPL does NOT inherit `current-prompt-read`**
  set during `-f` loading. Parameters are thread-local and the
  interactive loop runs inside a fresh continuation barrier, so a
  naive `racket -i -f integration.rkt` silently reverts to the
  default `> ` prompt. Fix: the integration script calls
  `(read-eval-print-loop)` itself after wiring up the parameter
  — we drive the REPL from our own code rather than from the
  binary's built-in `-i` path. See `adapters/racket.yaml` and
  `ShellIntegration/integration.rkt`.
- **CCL hard-locks `ccl::print-listener-prompt` redefinition** by
  default. Binding `ccl:*warn-if-redefine-kernel*` to `nil`
  downgrades the check to a no-op so
  `(setf (symbol-function 'ccl::print-listener-prompt) ...)` takes
  effect. See `ShellIntegration/integration.lisp` (gitignored
  locally — CCL binary blocked by AppLocker on this box).
- **F# Interactive (`dotnet fsi`) quirks — all four required to
  get fsi running under ConPTY:**
  1. `--gui-` is mandatory. The default is `--gui+`, which runs
     interactions on a Windows Forms event loop that silently
     fails to initialise under ConPTY. Without `--gui-`, fsi
     accepts the post-script-load prompt but ignores all stdin
     afterwards.
  2. `--readline-` is strongly recommended. Without it, fsi
     rewrites the prompt line via cursor positioning on every
     keystroke, polluting the captured stream.
  3. `--use:<file>` with any script (even an empty one) is
     required to keep fsi alive. Plain `dotnet fsi` under ConPTY
     exits within ~80ms before the first prompt is drawn —
     some dotnet-host TTY-detection edge case. An empty
     integration.fsx (literal comments only, zero top-level
     statements) is enough; any top-level F# expression would
     trigger a `val it: ... = <result>` emission that the regex
     prompt tracker would mis-resolve as the first user
     command's output.
  4. `ready.delay_after_inject_ms: 800`. Even with the three
     flags above, fsi prints its post-script-load prompt ~200ms
     before the eval loop wires up stdin. Without the settle
     window, the test runner's first eval races the startup
     and jshell-like races can happen. The field is repurposed
     for regex strategy (see "next-session candidate work #3"
     for the planned cleanup).
- **No adapter can launch binaries from `%USERPROFILE%` on this
  box under ConPTY** because corporate AppLocker blocks the
  spawn at `CreateProcessW` time (error 5 = ACCESS_DENIED),
  surfacing ONLY when ConPTY attaches the pseudoconsole —
  `Process.Start` from the same user-dir path works fine,
  confirming it's specifically the splash `--console`-mode
  ConPTY spawn being filtered. Binaries in `C:\Program Files\**`
  are whitelisted and work. Concretely this means:
  - racket (Program Files) ✅
  - python / node / pwsh / bash / cmd (Program Files / Git /
    System32) ✅
  - fsi / jshell (Program Files\dotnet, Program Files\Microsoft\jdk-21) ✅
  - CCL (`%USERPROFILE%\ccl`) ❌ — adapter source kept
    gitignored for machines where the policy allows it.
- **BOM in commit messages**: FIXED in `fix(tools): write files
  as UTF-8 without BOM`. `FileTools.cs` now uses a shared
  `UTF8Encoding(false)` for every write, so `mcp__splash__write_file`
  output pipes cleanly into `git commit -F`. Reads already
  detect+strip BOM via `detectEncodingFromByteOrderMarks: true`
  so round-tripping pre-BOM files still works.
- **`mcp__splash__edit_file` on CRLF files**: the splash-dev MCP
  tool's `edit_file` fails to match `old_string` on CRLF-terminated
  files. Use Claude Code's built-in `Edit` tool for splash's own
  source (which is CRLF per the .gitattributes text=auto policy)
  until splash's edit_file normalises line endings before search.
- **ABCL 1.9.2 + JDK 21 virtual-threading introspection warning.**
  On cold start ABCL 1.9.2 prints a red "Failed to introspect
  virtual threading methods: java.lang.reflect.InaccessibleObjectException"
  warning before the banner. The warning is cosmetic — ABCL was
  compiled against a JDK with open `java.lang` module access and
  JDK 21 tightened it — but it pollutes the pre-banner output
  window and would confuse the ready-phase settle. Fix: the
  adapter's `command_template` passes
  `--add-opens java.base/java.lang=ALL-UNNAMED` to java.exe,
  which silences the warning. Affects ABCL on any JDK 16+.
- **ABCL default prompt is `CL-USER(N): `, not `? `.** ABCL uses
  an Allegro-style package-qualified prompt with a monotonic form
  counter. The splash integration overrides
  `top-level::*repl-prompt-fun*` to emit a literal `? ` instead
  so both Common Lisp adapters (CCL and ABCL) share the same mode
  regex `^\? $` without per-impl branching. If a future tweak
  ever restores ABCL's native prompt format, the mode regex needs
  to grow an alternation.
- **ABCL's debugger is `system::debug-loop`, not the same
  mechanism as `top-level::*repl-prompt-fun*`.** The CCL-style
  `N > ` nested break loop prompt pattern and the `level_capture`
  mode regex do **not** port directly to ABCL — ABCL's debug-loop
  prints a separate prompt that the top-level hook doesn't see.
  The ABCL adapter ships with only the `main` mode declared;
  debugger mode is deferred until someone researches the
  `debug-loop` prompt mechanism against a live break scenario.
  Errors today just leave the REPL sitting in `main` mode with
  the error text printed to stdout.
- **`CancellationTokenRegistration.Dispose` blocks from inside
  the callback.** When a CTS-registered delegate calls back into
  code that disposes its own registration, the `Dispose` call
  waits for the callback to finish — which is the same thread
  currently inside the callback. Instant deadlock. First hit it
  in `FlipToCacheMode` wiring: the 170 s preemptive timer is
  registered as `Register(FlipToCacheMode)`, and `FlipToCacheMode`
  originally disposed `_timeoutReg` inline. Test 10 "Shell returned
  to standby after Ctrl+C" manifested as a wedged shell that
  never returned to standby. Fix: leave disposal to `Resolve`'s
  cache branch and `AbortPending`, where the command has finished
  running and there's no active callback to wait for.
- **`NormalizeShellFamily` stays.** It looks like a hardcoded
  shell-family helper but it's the path-to-registry-key normaliser
  that `AdapterRegistry.Find` itself uses as a lookup key —
  `Path.GetFileNameWithoutExtension(shell).ToLowerInvariant()`. The
  last *real* shell-family literal in `ConsoleManager`
  (`IsWindowsNativeShellFamily`) was replaced by `cwd_format` in
  commit `7dfb533`.

---

## Commit history at a glance

Phase B → C → C+ → regex-strategy → cache-on-busy-receive → ABCL
arc is ~30 commits, each a self-contained story. Newest first:

```
cd1cef4  feat(adapters): ship Armed Bear Common Lisp (ABCL) adapter
b367e99  docs: reflect cache-on-busy-receive round in HANDOFF + CHANGELOG
af4e5e5  feat(worker): cache-on-busy-receive to recover flipped command results
1759404  feat(adapters): ship Apache Groovy (groovysh) adapter
97e80c6  feat(detector): substitute CSI Cursor Forward (CUF) with spaces
8ead1b6  feat(worker): %ENVVAR% expansion + generic command_template fallback
b8c8e62  docs(handoff): reflect regex-strategy round — fsi, jshell, CSI-aware detector
2f543bd  feat(adapters): ship Java jshell adapter
cd76098  feat(adapters): ship F# Interactive (fsi) adapter
7823ab2  feat(worker): wire regex prompt strategy + process.executable override
3c4b081  feat(detector): CSI-aware RegexPromptDetector
121d2b5  docs(handoff): replace PENDING placeholder with ac1929f hash
ac1929f  feat(schema): runtime modes graph walker closes §18 Q2
654225c  docs(handoff): mark §18 Q1 closed and balanced_parens counter live
ed3e7fa  feat(schema): runtime balanced_parens counter closes §18 Q1
a8f56ed  chore(worker): silence CA1416 on GetRegistryPathExt
81efbcd  docs(handoff): reflect phase C+ state — 7 adapters, 385 assertions
589feff  docs(schema): resolve §18 Q1 and Q2 from adapter evidence
aff1249  feat(python): declare pdb as an auto_enter debug mode
bc68271  feat(adapters): ship Racket REPL adapter with OSC 633 via current-prompt-read
48f5197  feat(cli): add --probe-adapters and truncate --list-adapters summary
b946d3e  feat(schema): tune WaitForOutputSettled via adapter.ready.output_settled_*
44de5c8  fix(tools): write files as UTF-8 without BOM
73026ca  docs: add HANDOFF.md as the session entry-point document
10459f2  feat(tests): run each adapter's probe as a pre-flight before tests
9806574  feat(cli): add --list-adapters to print what the registry loaded
c6d2732  chore(repo): add .gitattributes to normalize line endings
7dfb533  feat(schema): promote IsWindowsNativeShellFamily to capabilities.cwd_format
f85928c  feat(tests): run each adapter's tests: block against a real worker
60ef8c8  refactor(conpty): unify env block construction, apply overrides in clean-env path
4495e81  feat(adapters): ship Node.js REPL adapter with OSC 633 via displayPrompt hook
061bb42  feat(python): multi-line command delivery via _splash_exec_file tempfile
529dff6  feat(adapters): load external YAMLs from ~/.splash/adapters
aaf9ed1  feat(adapters): ship Python REPL adapter with OSC 633 via sys.ps1 hook
8fb4802  refactor(worker): delete LoadEmbeddedScript dead fallback (milestone 2j)
17bfd61  refactor(worker): delete IsPowerShellFamily / IsUnixShell / EnterKeyFor
6266685  feat(adapters): BuildCommandLine reads command_template from YAML
705c2e7  feat(adapters): post-prompt drain stable_ms from adapter
08ad05f  feat(adapters): user-busy detector gated on capabilities + tuning from YAML
2c8dc42  feat(adapters): ready-phase branching reads adapter.Ready fields
```

Read them bottom-up if you want the phase B narrative, top-down if
you want to see the most recent polish first. Every commit was
live-verified via the splash-dev hot-reload loop (sitter_kill →
Build.ps1 → lazy respawn → actually run the new feature).
