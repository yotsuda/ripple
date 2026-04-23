# Architecture

How ripple is put together, for anyone reading the code for the first
time. Durable reference — not a changelog. For what's in-flight or
box-local, see `scratch/HANDOFF.md`; for the normative schema
contract, see `adapters/SCHEMA.md`.

## How it works (6 bullets)

1. **Proxy / worker split.** `ripple.exe` in MCP mode is the proxy
   that serves AI tool calls. When the AI asks to open a shell, the
   proxy launches `ripple.exe --console <shell>` in a new window —
   that's the worker, which owns the ConPTY pseudoconsole (Windows) or
   forkpty master (Unix) and PTY I/O. Proxy and worker talk over a
   Named Pipe (`RP.{proxyPid}.{agent}.{consolePid}`) using framed JSON
   RPC.

2. **Adapter-driven launch.** `ConsoleWorker.BuildCommandLine` reads
   `adapter.process.command_template` and expands `{shell_path}`,
   `{init_invocation}`, `{prompt_template}`, `{tempfile_path}` into
   the final `CreateProcessW` / `posix_spawn` command line. The worker
   also reads `adapter.process.env` (merged into the OS environment
   block by `ConPty.cs` regardless of inherit vs clean),
   `adapter.ready.*` for the startup synchronisation flow (including
   the tunable `output_settled_{min,stable,max}_ms` knobs on
   `WaitForOutputSettled`), `adapter.init.*` for integration-script
   delivery, `adapter.input.line_ending` for how Enter is written to
   the PTY, `adapter.output.input_echo_strategy` for how input echo
   is stripped from captured output, and
   `adapter.capabilities.{user_busy_detection,cwd_format,...}` for
   feature flags that change runtime behaviour.

3. **OSC 633 is the tracker's primary language; `prompt.strategy:
   regex` is the escape hatch.** Shell-integration adapters emit OSC
   633 sequences (A = prompt start, B = input start, C = command
   executing, D;N = command finished with exit code N, P;Cwd=... =
   cwd update) from their integration script. The worker parses these
   via `OscParser`, `CommandTracker` slices the output buffer between
   C and D, and the MCP response carries output + exit code + cwd
   back to the AI. REPL adapters (python, node, racket, ccl, abcl)
   install hooks that emit the same OSC events —
   `sys.ps1.__str__` for Python, `displayPrompt` + out-of-band
   `process.stdout.write` for Node, `current-prompt-read` override
   for Racket, the locked `ccl::print-listener-prompt` override for
   CCL, `top-level::*repl-prompt-fun*` for ABCL. Adapters whose host
   has NO prompt-replacement API (fsi, jshell, duckdb, psql) declare
   `prompt.strategy: regex` and let `RegexPromptDetector` synthesize
   the equivalent PromptStart / CommandFinished events from a regex
   match against the visible text. The detector is CSI-aware: it
   strips cursor positioning and colour escapes before matching and
   translates match positions back to original-byte coordinates so
   the downstream tracker sees one coherent event stream regardless
   of which strategy fired. The tracker has exactly one code path
   for all three cases.

4. **External adapters override embedded ones.** At startup,
   `AdapterRegistry.LoadDefault()` merges `Ripple.adapters.*.yaml`
   (embedded resources, baked into the binary at build time) with
   any `*.yaml` dropped into `~/.ripple/adapters/`. External adapters
   override embedded ones of the same name, with the override logged
   in the startup report visible in `ripple --list-adapters`. The
   external path resolves `script_resource` relative to the YAML's
   directory first, then falls back to the embedded
   `ShellIntegration/*` resources.

5. **Test contract gate.** `ripple --adapter-tests` runs every loaded
   adapter's `tests:` block via `AdapterDeclaredTestsRunner`. Missing
   interpreters (no zsh on this box, no PG server for psql) are
   soft-skipped so CI stays green on partial toolchains. An adapter's
   `probe` runs as a synthetic first test — a broken adapter fails
   fast instead of flooding the output with downstream failures. The
   same probe loop is reachable standalone via
   `ripple --probe-adapters`. `ripple --test --e2e` also runs the
   pre-existing pipe-protocol E2E suite + multi-shell cross-
   verification, but two known flakes in `ConsoleWorkerTests.Run`
   (Ctrl+C standby, obsolete PTY alive) block it from reaching the
   declared suite on some boxes — use `--adapter-tests` standalone in
   those cases. `--test` without `--e2e` runs the unit suite only.

6. **Cache / drain salvage layer.** The MCP client can silently drop
   an in-flight response (user hits ESC; a new tool call lands while
   the previous one is still running; the protocol's own 3-minute
   ceiling). ripple can't detect client-side cancel directly, so
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
   which enumerates the agent's consoles, drains each one's list
   atomically, and renders the `StatusLine` the worker baked at
   `Resolve` time alongside the command output. Every MCP tool
   response — not just `execute_command` or `wait_for_completion` —
   surfaces any salvaged results, and the AI sees them without
   having to know what flipped or when.

## Routing invariants

- `execute_command`'s `shell` parameter is **required**. Omitting it
  returns an actionable error at the ShellTools layer before
  `ConsoleManager.ExecuteCommandAsync` is touched. Closes a silent
  failure mode where AI-in-REPL-context's next shell pipeline could
  have silently routed into the still-open REPL.
- Per-shell MRU stack. `AgentSessionState.ActivePidsByShell` is
  `Dictionary<shell path, List<pid>>`; index 0 is "active for this
  shell", index 1+ are MRU fallbacks. `shell=pwsh` looks up the pwsh
  stack only — cross-shell contamination is structurally impossible.
  When the top is busy, the walker falls back down the same shell's
  MRU list instead of picking an arbitrary standby; touch-on-use
  keeps recently-used consoles at the top.
- `LastActiveByShell` mirrors the split for dead-console cwd
  recovery: a freshly-dead pwsh's cwd can't leak into the next
  `shell=psql` call.
- The rolling global `ActivePid` slot still exists for legacy non-
  routing readers (peek default, get_status default console pick),
  kept in sync by `TouchActivePid` / `RemoveActivePid`.

## Reading list

### Always-on

| Doc | Why |
|---|---|
| `scratch/HANDOFF.md` | Current session state, unreleased work, machine-local setup |
| `adapters/SCHEMA.md` | Normative schema contract |
| `docs/GOTCHAS.md` | Active tripwires you'll hit if you touch the code |
| Git log (`git log --oneline -40`) | Phase narratives live in commit messages |

### Reference (read when you need depth)

| Area | Files |
|---|---|
| Schema types in C# | `Services/Adapters/AdapterModel.cs`, `AdapterStaticContext.cs` (AOT-safe YamlDotNet) |
| Loader & registry | `Services/Adapters/AdapterLoader.cs`, `AdapterRegistry.cs` (embedded + `~/.ripple/adapters/` merge with override semantics) |
| Worker launch / exec | `Services/ConsoleWorker.cs` — `BuildCommandLine` and `RunAsync`'s ready phase are the adapter-driven hotspots |
| Proxy (MCP-facing) | `Services/ConsoleManager.cs` — `AgentSessionState` + `PlanExecutionAsync` own the routing state machine |
| ConPTY + env merge | `Services/ConPty.cs` — unified env block builder applies `adapter.process.env` to both inherit-env and clean-env paths |
| Unix PTY | `Services/UnixPty.cs` — `posix_spawn` + `POSIX_SPAWN_SETSID` (fork+execvp from multi-threaded .NET SIGSEGVed on Ubuntu 24.04) |
| Integration scripts | `ShellIntegration/*.{ps1,bash,zsh,py,js,rkt,fsx,abcl.lisp}` — single source of truth for each shell's OSC 633 emitter |
| Adapter YAMLs | `adapters/*.yaml` — 18 embedded examples covering every schema section currently consumed |
| Regex prompt detector | `Services/RegexPromptDetector.cs` — CSI-aware, strips ANSI and substitutes cursor-to-col-1 with `\n` so adapter authors can write natural `^<prompt>$` patterns |
| Input echo stripping | `Services/ConsoleWorker.cs::StripCmdInputEcho` — walker handles CR/LF, ANSI escapes, and (in `fuzzy_byte_match`) a one-shot leading prompt-redraw consumer using the adapter's own prompt regex |
| Cache / drain layer | `Services/CommandTracker.cs` (`FlipToCacheMode`, `PreemptiveTimeoutMs`), `Services/ConsoleWorker.cs` (`_cachedResults`, `HandleExecuteAsync` busy-flip, `HandleGetCachedOutput`, `BuildStatusLine` → delegates to `Services/StatusLineFormatter.cs`), `Services/ConsoleManager.cs` (`CollectCachedOutputsAsync` / `WaitForCompletionAsync` consumers), `Tools/ShellTools.cs::AppendCachedOutputs` (universal wrapper) |
| OSC title / split-chunk buffer | `Services/ConsoleWorker.cs::ReplaceOscTitle(input, desiredTitle, ref pendingTail)` rewrites shell-emitted OSC 0/1/2 to match the proxy-supplied display name, buffering partial openers that straddle a PTY chunk boundary |
| Mode detector | `Services/ModeDetector.cs` — pure regex-over-tail-of-output, fed from `_tracker.GetRawRecentBytes()` in a short poll loop. See GOTCHAS for why not the OSC-C..D slice or VtLite snapshot |
| Declarative test runner | `Tests/AdapterDeclaredTestsRunner.cs` — how each adapter's `tests:` block becomes a live worker assertion |
| Existing E2E plumbing | `Tests/ConsoleWorkerTests.cs` — `WaitForPipeAsync` / `SendRequest` are `internal` for runner reuse |

## Design patterns baked into the adapter corpus

**Groovy pattern** (invoked by `groovy.yaml` and `abcl.yaml`). Any
JVM-hosted REPL is launched as `java.exe -jar
%LOCALAPPDATA%\ripple-deps\<lang>\payload.jar ...`, keeping the only
spawned executable inside a whitelisted `C:\Program Files\**` path
while the jar payload loads as regular classfiles from user-dir.
Future JVM REPLs (Clojure, Kotlin REPL, JRuby, Jython, Scala) pick
this up for free by cloning `abcl.yaml` and swapping the jar path +
prompt regex / hook.

**Regex prompt strategy** (invoked by `fsi.yaml`, `jshell.yaml`,
external `duckdb.yaml`, external `psql.yaml`). REPLs without a prompt-
replacement API declare `prompt.strategy: regex` with a `primary`
pattern; `RegexPromptDetector` synthesises the OSC events. Works for
any REPL whose prompt is a recognisable literal or patterned string.

**rc_file integration delivery** (invoked by `zsh.yaml`). Shells that
deadlock on PTY-injection of the integration script declare
`init.delivery: rc_file` with `dir_env_var` and `file_name`. The
worker stages the script at the declared path before `CreateProcessW`
and exports the env var into the child, so the shell sources it as
part of its own startup. Extends to any future shell with a similar
config-dir env var (fish: `XDG_CONFIG_HOME` + `fish/config.fish`).
