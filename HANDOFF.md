# Session handoff — start here

Entry point for any future Claude Code (or human) session walking into
this repo cold. Read this first, then follow the reading list below
for whatever depth you need. Updated after the phase C+ punch-list
cleared on 2026-04-14.

---

## Current state (1 paragraph)

**splash** is a declarative adapter framework that exposes any
interactive process (shells, REPLs, eventually debuggers) to an AI
via MCP over ConPTY/forkpty. Phase B (YAML-drive the existing shell
runtime), phase C (framework generalisation), and the phase C+
punch list (Racket adapter, pdb mode declaration, `--probe-adapters`
CLI, `ready.output_settled_*` timing knobs, BOM fix on
`FileTools.WriteFile`, `--list-adapters` summary truncation,
CA1416 cleanup, **runtime `balanced_parens` counter with reader-
macro extensions**) are all complete. **7 adapters ship embedded**
(pwsh, bash, zsh, cmd, python, node, racket). **411 assertions**
pass on `--test --e2e` (300 unit + 79 pre-existing E2E + 32
adapter-declared). Zero shell-family literals survive in the C#
runtime outside the registry key normaliser. Schema §18 Q1
(balanced_parens vs reader macros) is **closed** by the runtime
counter and `char_literal_prefix` / `datum_comment_prefix` schema
extensions; §18 Q2 (exit_commands.effect enum) is **closed** by
the python adapter's pdb mode. Q3 and Q4 are untouched. Schema
is still intentionally `v1 draft` until the `modes` graph walker
is no longer declarative-only — that's the last big runtime
plumbing gap before v1 freeze.

---

## Warm-up checklist (30 seconds)

```powershell
cd C:\MyProj\splash
git log --oneline -30                              # last 30 commits — phase B/C/C+ arc
./bin/Debug/net9.0/splash.exe --list-adapters      # 7 adapters + their capabilities
./bin/Debug/net9.0/splash.exe --probe-adapters     # opt-in pre-flight, one probe.eval per adapter
./bin/Debug/net9.0/splash.exe --test --e2e         # 411 / 411 green, zsh SKIP expected
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
| Integration scripts | `ShellIntegration/*.{ps1,bash,zsh,py,js}` — the single source of truth for each shell's OSC 633 emitter |
| Adapter YAMLs | `adapters/*.yaml` — 6 live examples covering every schema section that's currently consumed |
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

3. **OSC 633 is the tracker's language.** Shell-integration adapters
   emit OSC 633 sequences (A = prompt start, B = input start, C =
   command executing, D;N = command finished with exit code N,
   P;Cwd=... = cwd update) from their integration script. The worker
   parses these via `OscParser`, `CommandTracker` slices the output
   buffer between C and D, and the MCP response carries output + exit
   code + cwd back to the AI. REPL adapters (python, node, racket)
   install hooks that emit the same OSC events — `sys.ps1.__str__`
   for Python, `displayPrompt` + out-of-band `process.stdout.write`
   for Node, `current-prompt-read` override for Racket — so the
   tracker has exactly one code path for both shells and REPLs.

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

---

## Next-session candidate work

Phase C+ is clear. The only remaining gap before v1 freeze is the
`modes` graph walker. Roughly in order of payoff per risk:

1. **Runtime `modes` graph walking** — the unfinished half of
   §18 Q2 and the last big runtime plumbing gap. The python
   adapter declares pdb as an auto_enter mode, but `ConsoleWorker`
   does not walk the graph, enforce exit commands, or emit mode-
   change events; `AdapterDeclaredTestsRunner` treats
   `expect_mode` / `expect_level` as deferred fields. Adding this
   means tracking mode state across commands (which mode am I in
   now? did this command trigger a mode change?). Good first
   step: emit a `currentMode` field in the JSON response from
   `get_status` and `execute`, populated by re-running the mode
   regexes against the tail of the output buffer. Richer graph
   walking (exit_commands enforcement, expect_mode assertions)
   can follow as a second pass. Once this lands, schema v1 can
   freeze.

2. **More reader-macro-heavy Lisp adapters (SBCL / GHCi)** — stress
   the Q1 fields further (now that the runtime counter is live)
   and provide a second evidence point for Q4
   (`balanced_parens: { preset: lisp }`). Requires installing
   an interpreter (SBCL is smallest for CL; GHCi pulls in GHC
   which is heavy). Each new adapter is also a smoke test for
   `BalancedParensCounter` against a different reader-macro
   surface area. Defer until a test box already has one of
   these toolchains.

3. **Async-output handling (§18 Q3)** — `redraw_detect` is the
   only defined strategy for `output.async_interleave.strategy`
   and neither in-tree adapter exercises it. A future BEAM
   (iex / erlang shell) or Go REPL adapter would surface whether
   a single strategy covers both async families or if per-family
   variants are needed. Blocked on adding one of those adapters.

4. **`balanced_parens: { preset: lisp }` (§18 Q4)** — once a
   second Lisp adapter ships and duplicates Racket's
   `balanced_parens` block almost verbatim, factor the common
   bits into a registry preset that an adapter can reference by
   name. Cosmetic / DRY improvement, not a runtime change.
   Blocked on item 2.

5. **`.gitattributes` renormalisation** — still held off. `git
   add --renormalize .` would touch every tracked file and
   pollute blame history. Do this only if there's a separate
   reason to burn a blame entry. Not worth tackling in isolation.

User policy as of 2026-04-14: **schema v1 remains unfrozen** —
the `balanced_parens` counter is now live (Q1 closed), so the
last gate is the `modes` graph walker for Q2 to be fully closed
at the runtime layer. Once that lands, freeze.

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
- **`NormalizeShellFamily` stays.** It looks like a hardcoded
  shell-family helper but it's the path-to-registry-key normaliser
  that `AdapterRegistry.Find` itself uses as a lookup key —
  `Path.GetFileNameWithoutExtension(shell).ToLowerInvariant()`. The
  last *real* shell-family literal in `ConsoleManager`
  (`IsWindowsNativeShellFamily`) was replaced by `cwd_format` in
  commit `7dfb533`.

---

## Commit history at a glance

Phase B → C → C+ is ~25 commits, each a self-contained story.
Newest first:

```
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
