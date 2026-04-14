# Session handoff — start here

Entry point for any future Claude Code (or human) session walking into
this repo cold. Read this first, then follow the reading list below
for whatever depth you need. Updated after the phase C completion on
2026-04-14.

---

## Current state (1 paragraph)

**splash** is a declarative adapter framework that exposes any
interactive process (shells, REPLs, eventually debuggers) to an AI
via MCP over ConPTY/forkpty. Phase B (YAML-drive the existing shell
runtime) and phase C (framework generalisation: external adapter
loading, two REPLs, unified env block, tests runner, schema
evolution) are both complete. 6 adapters ship embedded (pwsh, bash,
zsh, cmd, python, node). 341 assertions pass on `--test --e2e`
(252 unit + 63 pre-existing E2E + 26 adapter-declared). Zero
shell-family literals survive in the C# runtime outside the
registry key normaliser. Schema is intentionally left as
`v1 draft` until a Lisp-family or debugger adapter surfaces evidence
for the open questions in [SCHEMA.md §18](adapters/SCHEMA.md).

---

## Warm-up checklist (30 seconds)

```powershell
cd C:\MyProj\splash
git log --oneline -20                              # last 20 commits — the phase B/C arc
./bin/Debug/net9.0/splash.exe --list-adapters      # 6 adapters + their capabilities
./bin/Debug/net9.0/splash.exe --test --e2e         # 341 / 341 green, zsh SKIP expected
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
   for the startup synchronisation flow, `adapter.init.*` for the
   integration script delivery, `adapter.input.line_ending` for how
   Enter is written to the PTY, `adapter.output.input_echo_strategy`
   for how input echo is stripped from captured output, and
   `adapter.capabilities.{user_busy_detection,cwd_format,...}` for
   the feature flags that change runtime behaviour.

3. **OSC 633 is the tracker's language.** Shell-integration adapters
   emit OSC 633 sequences (A = prompt start, B = input start, C =
   command executing, D;N = command finished with exit code N,
   P;Cwd=... = cwd update) from their integration script. The worker
   parses these via `OscParser`, `CommandTracker` slices the output
   buffer between C and D, and the MCP response carries output + exit
   code + cwd back to the AI. REPL adapters (python, node) install
   hooks that emit the same OSC events — `sys.ps1.__str__` for
   Python, `displayPrompt` + out-of-band `process.stdout.write` for
   Node — so the tracker has exactly one code path for both shells
   and REPLs.

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
   instead of flooding the output with downstream failures.

---

## Next-session candidate work

Roughly in order of payoff per risk, with the framework rationale:

1. **Lisp-family REPL adapter (ghci / sbcl / clisp / racket)** —
   the highest-information next step because it forces the schema
   through `balanced_parens` (SCHEMA §18 Q1) and
   `multiline_delivery: wrapper` (ghci's `:{ ... :}` form). Needs a
   Lisp interpreter installed on the test box; if none are available,
   downgrade to a machine that has one. Deliverable: `adapters/ghci.yaml`
   or equivalent, a `ShellIntegration/integration.hs` or similar,
   and the RegexPromptDetector finally wired into the worker for
   strategy=regex adapters.

2. **Debugger adapter (gdb / pdb / lldb)** — exercises
   `modes` + `exit_commands.effect` (SCHEMA §18 Q2). Bigger than
   Lisp because `modes` is currently declarative-only — the worker
   has no concept of "mode transition", so this requires runtime
   plumbing on top of a new adapter. First version could stub
   `modes` support behind a feature flag and grow it once the
   pattern is clear.

3. **`probe.eval` at load time (opt-in CLI flag)** — quick health
   check before the MCP server accepts connections. Small scope
   (reuse `AdapterDeclaredTestsRunner`'s probe path), self-contained,
   useful for catching broken adapters on startup instead of first
   use. Gate behind `--probe-adapters` so default startup stays
   fast.

4. **`WaitForOutputSettled` timing from schema** — the last
   hardcoded numbers in `ConsoleWorker` (`2s minimum + 1s stable +
   30s deadline`). None of the current 6 adapters would tune these
   but a Lisp adapter with a slow compiler startup might want to.
   New schema fields: `ready.output_settled_min_ms`, `_max_ms`,
   `_stable_ms`. Additive, non-breaking.

5. **Fix UTF-8 BOM in splash's own `write_file` MCP tool.** Every
   file Claude writes via `mcp__splash__write_file` gets a BOM
   prepended, which pollutes commit-message subject lines unless
   stripped before `git commit -F`. The fix belongs in `FileTools.cs`
   (change the file-write to use `new UTF8Encoding(false)`), not in
   every caller. Low risk but touches MCP wire behaviour — verify
   that no consumer depends on the BOM.

6. **Polish:** `--list-adapters` summary truncation for long
   YAML `description: >` multi-line folds; `.gitattributes`
   renormalisation of existing files via `git add --renormalize .`
   (held off so far because it would touch every tracked file and
   pollute blame history — do this only if there's a separate
   reason to burn a blame entry).

User policy as of 2026-04-14: **schema v1 remains unfrozen** —
don't stamp `v1 stable` until at least one Lisp-family or debugger
adapter has exercised the §18 open questions from evidence rather
than theory.

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
- **BOM in commit messages**: splash's `write_file` MCP tool emits
  UTF-8 with BOM. Strip before `git commit -F`:
  `$b = [IO.File]::ReadAllBytes($p); if ($b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { [IO.File]::WriteAllBytes($p, $b[3..($b.Length-1)]) }`
  Track as a TODO to fix in the MCP tool itself; see candidate #5.
- **`NormalizeShellFamily` stays.** It looks like a hardcoded
  shell-family helper but it's the path-to-registry-key normaliser
  that `AdapterRegistry.Find` itself uses as a lookup key —
  `Path.GetFileNameWithoutExtension(shell).ToLowerInvariant()`. The
  last *real* shell-family literal in `ConsoleManager`
  (`IsWindowsNativeShellFamily`) was replaced by `cwd_format` in
  commit `7dfb533`.

---

## Commit history at a glance

The phase B/C arc is ~15 commits, each a self-contained story:

```
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
