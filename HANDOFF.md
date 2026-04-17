# Session handoff — start here

Entry point for any future Claude Code (or human) session walking into
this repo cold. Read this first, then follow the reading list below
for whatever depth you need. Updated 2026-04-17 after the **zsh
recovery round** — MSYS2 zsh now runs under ConPTY via a new
`init.delivery: rc_file` schema mode, WSL-bash SKIP on boxes with
no installed distro was fixed via `executable_candidates`, the dead
`init.inject` sub-schema was removed, and the ccl / abcl top-level
`?`-prompt leak was plugged in the post-primary delta cleaner. HEAD
is `e5fec38`. **18 adapters ship embedded** in the 0.8.0 release,
with `perldb` / `jdb` joining as the first `family: debugger`
adapters (external `jdb-hello` as proof of ~/.ripple/adapters/ still
loads alongside the embedded ones).

---

## Current state (1 paragraph)

**ripple** is a declarative adapter framework that exposes any
interactive process (shells, REPLs, **debuggers**) to an AI via MCP
over ConPTY/forkpty. Phase B (YAML-drive the existing shell runtime),
phase C (framework generalisation), phase C+, the regex-strategy
round (CSI-aware `RegexPromptDetector`, `process.executable` override,
**F# Interactive** and **Java jshell** adapters), the
**cache-on-busy-receive salvage layer** (multi-entry per-console
cache list, `FlipToCacheMode`, 170 s preemptive timeout, universal
drain wrapper on every MCP tool), the **ABCL adapter round**
(JVM-hosted Common Lisp via the Groovy pattern), the v0.7.0
**polish round** (six bug fixes across `signals.interrupt`, OSC
title split-chunk buffer, nested datum comments, focus theft,
`input.clear_line`, and `ModeDetector` input source), the
**0.8.0 debugger-adapter round** (`family: debugger`, **perldb**
and **jdb** landing as the first two, plus **pdb**, **sqlite3**,
**lua**, **deno** REPLs, user-input hold during AI command
execution, `SW_HIDE` for test workers), and the **2026-04-17 zsh
recovery round** (MSYS2 zsh under ConPTY via `init.delivery:
rc_file`, bash Git-Bash-first via `executable_candidates`, dead
`init.inject` block removed, ccl/abcl `?`-prompt leak plugged) are
all complete.
**18 adapters ship embedded** (pwsh, bash, zsh, cmd, python, node,
racket, ccl, abcl, fsi, jshell, groovy, **perldb, jdb, pdb,
sqlite3, lua, deno**) plus `jdb-hello` as the first external
adapter example in `~/.ripple/adapters/`. **111 passed, 0 failed**
on `--adapter-tests` (as of `e5fec38`) modulo the known
jdb-hello/next flake. Two pre-existing `ConsoleWorkerTests.Run`
flakes (Ctrl+C standby, obsolete PTY alive) still block
`--test --e2e` from reaching the declared suite — use
`--adapter-tests` standalone to exercise that layer and ignore
the flake gate.
Two pre-existing `ConsoleWorkerTests.Run` flakes (Ctrl+C standby,
obsolete PTY alive) still block `--test --e2e` from reaching the
declared suite — use `--adapter-tests` standalone to exercise
that layer and ignore the flake gate. Zero shell-family literals
survive in the C# runtime outside the registry key normaliser.
Schema §18 Q1 (balanced_parens vs reader macros) is **empirically
closed** — 58 `BalancedParensCounter` assertions including the
nested-datum-comment bug the 2026-04-15 fix resolved. Schema §18
Q2 (auto_enter + nested + level_capture) is **empirically closed**
— the `ModeDetector` input-source fix plus a 4-test chain against
CCL's break loop (`(error ...)` → `1 >`, nested → `2 >`,
`:pop` → `1 >`, `:pop` → main) verify the path end-to-end against
a live REPL, and those tests are now in CI (not local-only) as of
v0.7.0. Q3 and Q4 remain untouched — both are blocked on a
BEAM/Go-style adapter, not on schema gaps. **The schema is ready
to freeze as `v1 stable`** the next time someone is willing to
stamp it; no remaining runtime gates.

---

## Warm-up checklist (30 seconds)

```powershell
cd C:\MyProj\ripple
git log --oneline -50                                     # phase B/C/C+ + regex-strategy + cache-on-busy-receive + ABCL + polish round + 0.8.0 debugger round + 2026-04-17 zsh recovery
./bin/Debug/net9.0/ripple.exe --list-adapters             # 18 embedded (+ external jdb-hello if ~/.ripple/adapters/ is populated)
./bin/Debug/net9.0/ripple.exe --probe-adapters            # opt-in pre-flight, one probe.eval per adapter
./bin/Debug/net9.0/ripple.exe --test                      # unit tests only
./bin/Debug/net9.0/ripple.exe --adapter-tests             # declared-test run across every loaded adapter — 111 / 111 on green day
./bin/Debug/net9.0/ripple.exe --adapter-tests --only ccl  # single-adapter focus (ccl includes the 4-test debugger-mode chain)
./bin/Debug/net9.0/ripple.exe --adapter-tests --only zsh  # verifies the rc_file-delivery path end-to-end
# --test --e2e still blocked by the two pre-existing ConsoleWorkerTests.Run
# flakes (Ctrl+C post-interrupt standby, obsolete PTY alive) — see gotchas.
```

If the Debug binary is missing or stale:

```powershell
dotnet build -c Debug                              # fast local build
./Build.ps1                                        # AOT Release → dist/ripple.exe (slower)
```

`./Build.ps1` is what ripple-dev (via mcp-sitter) picks up for
hot-reload cycles during the session. If you use the `ripple-dev`
MCP server, remember the flow: `sitter_kill` to unlock the binary,
`./Build.ps1` via another MCP (pwsh) to rebuild, then any
`ripple-dev` tool call triggers lazy respawn of the fresh child.

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
| Loader & registry | `Services/Adapters/AdapterLoader.cs`, `AdapterRegistry.cs` (embedded + `~/.ripple/adapters/` merge with override semantics) |
| Worker launch / exec | `Services/ConsoleWorker.cs` — `BuildCommandLine` and `RunAsync`'s ready phase are the adapter-driven hotspots |
| Proxy (MCP-facing) | `Services/ConsoleManager.cs` — the last shell-family literal (`NormalizeShellFamily`) is a registry key normaliser, not a family check |
| ConPTY + env merge | `Services/ConPty.cs` — unified env block builder applies `adapter.process.env` to both inherit-env and clean-env paths |
| Integration scripts | `ShellIntegration/*.{ps1,bash,zsh,py,js,rkt,fsx,abcl.lisp}` — the single source of truth for each shell's OSC 633 emitter. fsi's integration.fsx is intentionally empty (just comments) because F# Interactive has no prompt-replacement API; see Gotchas below. The two Common-Lisp integrations (`integration.lisp` for CCL, `integration.abcl.lisp` for ABCL) differ only in which prompt hook they override — CCL uses `(setf (symbol-function 'ccl::print-listener-prompt) ...)` behind a kernel-redefine warning gate, ABCL uses a simple `(setf top-level::*repl-prompt-fun* ...)`. |
| Adapter YAMLs | `adapters/*.yaml` — 11 live examples embedded in the binary covering every schema section that's currently consumed. The `groovy` / `abcl` pair documents the **Groovy pattern**: invoke `java.exe` from Program Files with `-jar %LOCALAPPDATA%\ripple-deps\<lang>\payload.jar` so the only spawned executable sits in a whitelisted path while the jar payload loads as regular classfiles from user-dir. Any future JVM-hosted REPL (Clojure, Kotlin REPL, JRuby, Jython, Scala) picks this up for free by cloning `abcl.yaml` and swapping the jar path + prompt regex / hook. |
| Regex prompt detector | `Services/RegexPromptDetector.cs` — CSI-aware, strips ANSI escapes internally and substitutes cursor-to-col-1 positioning with `\n` so adapter authors can write natural `^<prompt>$` patterns. Used by fsi and jshell; future ConPTY-rendering REPLs (ghci, bb, etc.) inherit this for free. |
| Cache / drain layer | `Services/CommandTracker.cs` (`_cachedResults` list, `FlipToCacheMode`, `ConsumeCachedOutputs`, `BuildStatusLine`, `PreemptiveTimeoutMs`), `Services/ConsoleWorker.cs` (`HandleExecuteAsync` busy-flip path, `HandleGetCachedOutput` array serialisation), `Services/ConsoleManager.cs` (`CollectCachedOutputsAsync` / `WaitForCompletionAsync` array consumers, `MaxExecuteTimeoutSeconds`), `Tools/ShellTools.cs` (`AppendCachedOutputs` universal wrapper that every MCP tool funnels through). |
| OSC title / split-chunk buffer | `Services/ConsoleWorker.cs` — `ReplaceOscTitle(input, desiredTitle, ref pendingTail)` on the read-loop path rewrites shell-emitted OSC 0/1/2 to match the proxy-supplied display name. The `ref pendingTail` parameter buffers partial openers that straddle a PTY chunk boundary, so `\e]0;part-of` arriving in chunk N and `title\a` in chunk N+1 are reassembled and rewritten in one piece — without it the partial leaks to the visible terminal and the shell's title wins. `_oscTitlePending` on the worker carries the tail across calls. |
| Console focus / buffer flush | `Services/ProcessLauncher.cs` — `STARTF_USESHOWWINDOW + SW_SHOWNOACTIVATE` in `STARTUPINFOW` so spawned workers don't steal keyboard focus from the editor. `Services/Adapters/AdapterModel.cs` — `InputSpec.ClearLine` (nullable string, default null) is the bytes the worker writes to the PTY before each execute to wipe any user-typed line-editor buffer contents. Opted in for bash and zsh (readline/ZLE emacs default); everything else needs per-shell empirical verification. |
| Mode detector + input source | `Services/ModeDetector.cs` — pure regex-over-tail-of-output. `Services/ConsoleWorker.cs` — `HandleExecuteAsync` runs the detector against `_tracker.GetRawRecentBytes()` in a short poll loop, NOT against the OSC-C..D slice which can never contain the post-A prompt, and NOT against `GetRecentOutputSnapshot()` which runs the ring through VtLite and reshapes the final prompt into cell-addressed form that the `^<prompt>$` anchored regex can't match. Verified live via CCL's `1 >` / `2 >` break-loop chain. |
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

1. **Proxy / worker split.** `ripple.exe` in MCP mode is the proxy
   that serves AI tool calls. When the AI asks to open a shell, the
   proxy launches `ripple.exe --console <shell>` in a new window —
   that's the worker, which owns the ConPTY pseudoconsole and PTY I/O.
   Proxy and worker talk over a Named Pipe (`RP.{proxyPid}.{agent}.{consolePid}`)
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
   `AdapterRegistry.LoadDefault()` merges `Ripple.adapters.*.yaml`
   (embedded resources, baked into the binary at build time) with
   any `*.yaml` dropped into `~/.ripple/adapters/`. External adapters
   override embedded ones of the same name, with the override logged
   in the startup report so you can see it in `ripple --list-adapters`.
   The external path resolves `script_resource` relative to the
   YAML's directory first, then falls back to the embedded
   `ShellIntegration/*` resources.

5. **`ripple --test --e2e` is the contract gate.** It runs unit
   tests, the pre-existing pipe-protocol E2E suite, the multi-shell
   cross-verification, then finally walks every loaded adapter's
   `tests:` block via `AdapterDeclaredTestsRunner`. Missing
   interpreters (e.g. no zsh on this Windows box) are soft-skipped
   so CI stays green on partial toolchains. An adapter's `probe`
   runs as a synthetic first test — a broken adapter fails fast
   instead of flooding the output with downstream failures. The
   same probe loop is reachable standalone via
   `ripple --probe-adapters` (opt-in, no other tests).

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
candidates are extensions and external-dependency work.

**Closed since the last HANDOFF revision:**
- ccl was shipped embedded in v0.7.0 (`0007de9`) — items 4b and 10
  below are historical context only.
- bash adapter-tests on WSL-free dev boxes now pass via
  `process.executable_candidates` picking Git for Windows' bash
  first (`0b88b28`, 2026-04-17).
- MSYS2 zsh under ConPTY works via new `init.delivery: rc_file`
  schema (`f8e721d` hardcoded → `dd494b2` generalised to schema).
- Dead `init.inject` sub-schema removed (`5fe6a6f`).
- bash/zsh's `multiline_delivery` yaml value corrected from the
  pre-schema-era lie (`direct`) to the runtime-truthful `tempfile`
  (`e5fec38`).
- ccl / abcl top-level `?`-prompt leak in post-primary delta
  (`ad9d010`).

1. **Stamp `schema: 1 stable`** — purely a docs change. Update
   `adapters/SCHEMA.md` line 7 (`Status: **draft**.`) to
   `Status: **stable** (frozen 2026-XX-XX)` and bump the version
   note. Q1 and Q2 are both closed at the runtime layer; Q3 and
   Q4 are blocked on adapters ripple doesn't ship yet, not on
   schema gaps. User opted to defer this until there is a concrete
   reason to stamp it (2026-04-14: "まだやるメリットがない").

2. **Runtime `multiline_detect` gate** — the schema field has
   been declared by three adapters (racket: `balanced_parens`,
   fsi: `none`, jshell: `prompt_based`) but `ConsoleWorker` still
   does not consume it on the input path; `balanced_parens` only
   runs as a validator inside an adapter test helper, and
   `prompt_based` has no runtime meaning yet. Wiring it for real
   would let ripple reject syntactically incomplete AI input
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
   like `C:\tools\ghcup\bin` before ripple can spawn it via ConPTY.
   Alternatively, **Clojure** runs on the JVM and would ship via
   the Groovy pattern (clone `abcl.yaml`, swap jar + prompt
   regex) without needing a native binary. See also the note on
   CCL below — the policy may have relaxed and CCL could now be
   shipped embedded alongside ABCL.

4b. ~~**Confirm CCL policy status and decide on embedding.**~~
    ✅ DONE in v0.7.0 (commit `0007de9`): `adapters/ccl.yaml` and
    `ShellIntegration/integration.lisp` were un-gitignored and
    added to `ripple.csproj`'s embedded resources. CCL now ships
    embedded alongside ABCL and the debugger-mode chain is in
    CI. On boxes where the AppLocker block is still active, the
    probe soft-fails same class as a missing zsh.

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
     fast enough on this box. Investigate with `ripple --console
     pwsh.exe` manually + send_input `\x03` via another proxy.
   - `PTY still alive after obsolete state` — after sending a
     claim from a fake-higher proxy version, the worker marks
     itself obsolete and writes a banner, but the assertion that
     the PTY is still draining bytes fails on this run. The
     visible terminal *is* still alive (manual check), so this
     may be a probe-timing issue in the test harness rather than
     a real regression.
   Both flakes are pre-existing; the cache/drain round did not
   introduce them. They block `--test --e2e` from reaching the
   `AdapterDeclaredTestsRunner` because
   `ConsoleWorkerTests.Run` hard-exits on any failure. The
   `--adapter-tests` standalone CLI flag (added in `cd1cef4`)
   works around this by running the declared tests without the
   surrounding harness, but the flakes themselves remain
   un-root-caused. Next task: investigate each one with
   targeted instrumentation — log `get_status` return values
   during the post-Ctrl+C drain loop to see whether
   `_isAiCommand` / `_userCommandBusy` actually transitions;
   for the obsolete-claim case, confirm whether the worker PID
   is still alive after receiving the obsolete response.

10. ~~**Decide whether to un-gitignore `ccl.yaml` and ship it
    embedded.**~~ ✅ DONE in v0.7.0 (`0007de9`). Historical
    duplicate of item 4b — both were tracking the same shipping
    decision, now closed.

11. **`input.clear_line` opt-in for the remaining adapters.**
    Currently opted in for bash and zsh only (both use
    readline / ZLE emacs mode by default, empirically verified).
    pwsh, node, jshell, groovy each need per-shell smoke testing
    to confirm `\x01\x0b` or another kill-line sequence works
    before opt-in. python / fsi / racket / ccl / abcl stay null
    permanently (no line editor). A future session can walk the
    shortlist: start a console, manually type junk, issue an
    execute, confirm the junk is wiped. Low priority — the
    `SW_SHOWNOACTIVATE` focus fix + the 0.8.0 user-input hold gate
    (`bb7e4e3`) are the primary defenses.

12. **Unix / macOS parity — the biggest strategic lever.** Added
    to the candidate list on 2026-04-16 after reviewing the MCP
    shell competitive landscape (see
    `../../PowerShell.MCP/scratch/competitive-landscape-ja.md`).
    The gravity of the MCP shell ecosystem is overwhelmingly on
    Linux / macOS — DesktopCommanderMCP (~958k dl/year), iterm-mcp
    (~16k dl/year), every major PTY-based competitor, and the
    Python REPL family are all Linux/macOS first or exclusive.
    ripple is currently Windows-first and the Unix PTY path is
    marked experimental in README. Making Unix / macOS
    production-grade would open the larger half of the market
    while the differentiators (shared visible console, 12-adapter
    framework, OSC 633 lifecycle, auto-routing, re-claim) all
    translate cleanly.

    **What's already in place.** The forkpty code path exists in
    `ProcessLauncher.LaunchConsoleWorkerUnix` (via `setsid` + a
    login shell wrapper); `AdapterModel.Capabilities.CwdFormat`
    already has a `posix` case for `bash/zsh/sh`; every adapter
    except cmd (Windows-only by nature) is theoretically
    portable — bash/zsh are Unix-native, pwsh/fsi/jshell are
    cross-platform .NET/JVM binaries, python/node/racket are
    Unix-first, ccl/abcl/groovy all run on macOS and Linux.
    `InvariantGlobalization=true` + `PublishAot=true` are already
    set in `ripple.csproj`, so building for `osx-arm64` /
    `osx-x64` / `linux-x64` is a `dotnet publish -r <rid>` flag
    away.

    **What's untested (blockers for the "experimental" label to
    come off).** The forkpty path has never been exercised end-
    to-end on a real Unix host — "compiles and starts" is the
    current bar. Specific open questions: does
    `ConsoleManager.NormalizeShellFamily` map Unix shell paths
    correctly? Do the bash / zsh `integration.bash` / `.zsh`
    scripts actually inject their OSC 633 sequences under
    forkpty (pty_inject on Unix may need a different write
    strategy than on ConPTY)? Does `ReadOutputLoop`'s blocking
    `stream.Read` work against a forkpty master the same way it
    works against `_pty.OutputStream` on Windows? Does the user-
    busy detector's `process_polling` have a meaningful
    implementation on Unix, or does it need a separate code path
    (e.g. `/proc/<pid>/stat` on Linux)? Each of these is a
    day's worth of investigation plus a fix.

    **The hardest unsolved piece: visible terminal window on
    Unix.** On Windows, `CREATE_NEW_CONSOLE` gives ripple a
    literal user-facing console window for free — the "shared
    visible console" differentiator depends on this. There is no
    direct equivalent on Linux or macOS. Three paths forward,
    none of which ripple has today:
    - **macOS: Terminal.app / iTerm2 control.** iterm-mcp's
      AppleScript approach is proven for iTerm2; Terminal.app has
      a similar AppleScript surface. Implementation is per-
      terminal-emulator and macOS-only. Work estimate: 2-3 days
      each, probably start with Terminal.app (ubiquitous by
      default) and add iTerm2 as a second backend.
    - **Linux: spawn the terminal emulator as a subprocess.**
      `gnome-terminal -- ripple --console ...` or
      `alacritty -e ripple --console ...` gets the job done
      without AppleScript equivalents; each emulator has
      slightly different argument conventions so a detection
      layer is needed (parallel to how ripple already probes
      `$TERM_PROGRAM` on macOS in a few places). Work estimate:
      2-3 days for the top 3-4 emulators (gnome-terminal,
      konsole, xterm, alacritty).
    - **Avoided: headless PTY + Web UI.** Would match
      takafu/repl-mcp / amol21p/mcp-interactive-terminal, but
      abandons the "user and AI share the SAME visible terminal"
      differentiator. Don't ship this unless paths 1/2 turn out
      to be infeasible.

    **Estimated total scope for production-grade Unix/macOS
    support.** ~1-2 weeks of focused work: ~3-5 days on forkpty
    / OSC 633 / adapter sanity, ~3-5 days on terminal emulator
    spawn across 2-3 macOS and 3-4 Linux backends, ~2-3 days on
    CI matrix + GitHub Actions builds for `osx-*` / `linux-x64`,
    ~1 day of Homebrew / .deb packaging research (optional
    follow-up, not strictly v0.8 blocking). The adapter layer
    itself should stay mostly untouched.

    **Why this is item 12, not item 1.** No user has reported
    Unix/macOS issues yet, so the opportunity cost against
    continuing to polish Windows is real. But the competitive
    analysis strongly suggests this is the biggest strategic
    lever ripple has for growth beyond PowerShell.MCP's Windows-
    centric audience — possibly more impactful than any single
    adapter or schema cleanup on the rest of this list. Elevate
    to item 1 the moment a v0.8 milestone is planned.

User policy as of 2026-04-14: **schema is ready to freeze** but
the user opted not to stamp it until there's a concrete reason.
All four §18 questions are either closed (Q1, Q2) or blocked on
external adapters that ripple doesn't yet ship (Q3, Q4) — neither
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
  confirming it's specifically the ripple `--console`-mode
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
  `UTF8Encoding(false)` for every write, so `mcp__ripple__write_file`
  output pipes cleanly into `git commit -F`. Reads already
  detect+strip BOM via `detectEncodingFromByteOrderMarks: true`
  so round-tripping pre-BOM files still works.
- **`mcp__ripple__edit_file` on CRLF files**: the ripple-dev MCP
  tool's `edit_file` fails to match `old_string` on CRLF-terminated
  files. Use Claude Code's built-in `Edit` tool for ripple's own
  source (which is CRLF per the .gitattributes text=auto policy)
  until ripple's edit_file normalises line endings before search.
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
  counter. The ripple integration overrides
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
- **ModeDetector input source: raw ring bytes, not the OSC slice
  or VtLite snapshot.** The mode transition signal lives in the
  NEXT prompt (`1 > ` / `(Pdb) ` / `N] `), which arrives AFTER
  OSC A fires Resolve — so `cleanedOutput` (the OSC-C..D slice)
  can never contain it, and every mode transition silently fell
  through to the default. Worse, `GetRecentOutputSnapshot()`
  runs the ring through VtLite which reshapes the trailing prompt
  into cell-addressed coordinates that break `^<prompt>$`
  anchored regexes. `HandleExecuteAsync` now scans
  `_tracker.GetRawRecentBytes()` in a short 150 ms poll loop,
  breaking out as soon as a non-default auto_enter mode matches.
  Verified live against CCL's break loop (`(error ...)` →
  `1 > ` / `2 > ` / `:pop` chain). Python's pdb stays
  declarative-only because its `(Pdb) ` prompt bypasses
  `sys.ps1` and no OSC event fires for the transition — see
  `python.yaml` comment.
- **C# `\x07` is greedy.** `\x07after` parses as `\u07AF` + `ter`
  (four hex digits eaten), not BEL + `after`. Test strings that
  embed BEL terminators need to use `\a` instead — which is a
  single-character escape for `\u0007` and can't over-match.
  Bit me during the `ReplaceOscTitle` test coverage — two tests
  mysteriously failed against what looked like identical
  strings. Applies to `\x0e` (→ `\u0EAF`) and any other pairing
  where the literal continuation is a hex digit; always prefer
  named escapes (`\a`, `\b`, `\t`, `\r`, `\n`) or explicit
  `\u0007` when writing test fixtures.
- **OSC title sequences split across PTY read chunks.** The read
  loop's chunk size is ~4 KB, and a shell's PROMPT_COMMAND that
  writes `\e]0;long title\a` can easily land half in one chunk
  and half in the next. The pre-fix `ReplaceOscTitle` was
  stateless and emitted the first chunk's partial opener
  verbatim, leaving the visible terminal in "waiting for title
  termination" state — so when the terminator eventually
  arrived, the terminal committed the SHELL's title and
  ripple's desired title was clobbered. User-visible symptom:
  "owned-console window title sometimes not set correctly".
  Fix (`737f0a3`): `ref string pendingTail` parameter on
  `ReplaceOscTitle` carries the unterminated opener to the
  next call. `_oscTitlePending` on the worker owns the buffer.
  21 split-chunk unit assertions lock the behaviour down.
- **Node REPL single-threaded signal handling.** Node's event
  loop runs the user's JS and the Ctrl-C signal handler on the
  same thread. A sync `while (true) { ... }` loop blocks the
  event loop, so the signal handler never fires — sending
  `\x03` is a no-op. A pending top-level `await` has the same
  shape (nothing yields until the promise resolves). Adapter's
  `capabilities.interrupt` is `false` for this reason even
  though `signals.interrupt: "\x03"` is set: the byte exists
  for the rare case where Node DOES yield at a natural cooperative
  point, but AI clients shouldn't rely on it to rescue a runaway.
- **groovysh Ctrl-C is destructive.** Sending `\x03` to groovysh
  terminates the JVM and closes the shell outright, rather than
  unwinding the running command back to the prompt. No groovysh
  command-line flag has been found to downgrade this. The
  adapter declares `signals.interrupt: null` and
  `capabilities.interrupt: false` so MCP clients won't try
  to rescue a stuck Groovy command — the only safe recovery
  is `:quit` (via `lifecycle.shutdown.command`) or waiting
  for the command to finish.
- **Python basic REPL has no line editor.** `PYTHON_BASIC_REPL=1`
  disables readline, so the obvious-looking `input.clear_line:
  "\x01\x0b"` opt-in produces
  `SyntaxError: invalid non-printable character U+0001` —
  python parses the Ctrl-A byte as literal source code. Same
  for fsi with `--readline-`, racket with `-i`, CCL, and ABCL —
  none of them have a line editor the clear bytes can target.
  `clear_line` defaults to `null` (opt-in per adapter) and
  is only set for bash/zsh where empirical verification
  confirmed the readline/ZLE emacs default handles the bytes
  as no-ops on an empty buffer.
- **pdb's `(Pdb) ` prompt bypasses `sys.ps1`.** Python's pdb
  runs its own read-eval loop instead of going through the
  main REPL's input path, so the integration script's OSC-
  emitting prompt hook never fires for `(Pdb) `. An
  execute_command that triggers pdb.set_trace() hangs waiting
  for an OSC A that never comes, then returns timedOut=true.
  Documented in `python.yaml`'s pdb mode comment. Closing this
  would require installing a pdb-aware hook via
  `sys.monitoring` or a `pdb.Pdb` subclass — deferred until
  there's a concrete workflow that needs it.
- **`SW_SHOWNOACTIVATE` on CreateProcessW.** Spawning a worker
  with `CREATE_NEW_CONSOLE` alone makes Windows move keyboard
  focus to the new console window by default — if the user
  is typing in their editor when a console is launched, their
  keystrokes land in ripple's shell until they notice and
  re-focus the editor, and those buffered bytes get prepended
  to the next AI command. Setting `STARTF_USESHOWWINDOW +
  SW_SHOWNOACTIVATE` in `STARTUPINFOW` shows the window
  without activating it, so the user's focus stays put.
  Fixed in `c90d3f1`.
- **WSL bash needs an installed distribution to survive startup.**
  `C:\Windows\System32\bash.exe` is the WSL launcher. On a box
  where `wsl -l -v` says "has no installed distributions", the
  launcher exits ~150 ms after `CreateProcessW` (stderr is
  captured but the child is dead before the adapter can
  integrate). Before the 2026-04-17 round this made bash
  adapter-tests silently SKIP with "worker pipe never ready" on
  any WSL-free dev box. Fix: bash.yaml now declares
  `process.executable_candidates` with Git for Windows' bundled
  `C:\Program Files\Git\bin\bash.exe` as the first preference, so
  the worker picks Git Bash (or MSYS2 if Git isn't installed)
  before falling through to WSL. The WSL path still works when a
  distro *is* installed; dev boxes without one just don't hit it.
- **MSYS2 zsh under ConPTY deadlocks the pty_inject flow.** zsh's
  line editor (ZLE) buffers PTY-written bytes but treats neither
  `\n` nor `\r\n` as a reliable Enter from the worker's
  `WriteToPty`, so the `source '<tmpfile>'; rm -f '<tmpfile>'`
  injection never submits and `WaitForReady` times out (manifests
  as "worker pipe never ready"). Verified empirically against
  several line-ending variants; none submit. Fix: new
  `init.delivery: rc_file` schema mode stages integration.zsh as
  `<ZDOTDIR>/.zshrc` before `CreateProcessW` and exports `ZDOTDIR`
  into the child env. zsh sources `.zshrc` as part of its own
  startup, so `precmd` / `preexec` hooks are live by the time
  the first prompt is drawn — no PTY write involved. Extends to
  any future shell with an rc-directory env var (fish, etc.) by
  declaring `rc_file: { dir_env_var: XDG_CONFIG_HOME, file_name:
  fish/config.fish }`. See `dd494b2` for the schema and
  `f8e721d` for the original hardcoded fix that motivated the
  abstraction.
- **`init.inject` sub-block was dead documentation.** bash.yaml
  carried a nested `inject: { method, windows: { tempfile_prefix,
  tempfile_extension, wsl_path_template, msys_path_template,
  source_command_template }, unix: { ... } }` block that looked
  like the pty_inject flow was YAML-driven, but the runtime
  `InjectShellIntegration` hardcoded every one of those values
  (tempfile naming, path translation, source-command template)
  in C#. The yaml was parsed into `InjectSpec` objects that sat
  unread on `InitSpec.Inject`. Removed in `5fe6a6f` along with
  the three model classes (`InjectSpec` / `InjectWindowsSpec` /
  `InjectUnixSpec`) and their YamlSerializable registrations.
  The pty_inject delivery mode still works unchanged — nothing
  was consulting those fields at runtime.
- **`multiline_delivery` is schema-partial.** The generic
  tempfile-dispatch path
  (`adapter.Input.MultilineDelivery == "tempfile"` +
  `tempfile.invocation_template`) works for the 5 REPL adapters
  that declare it (python, node, racket, ccl, abcl). pwsh / cmd /
  bash / zsh all get tempfile behaviour via *hardcoded*
  `isMultiLinePwsh` / `isMultiLineCmd` / `isMultiLinePosix`
  branches in `HandleExecuteAsync` that take precedence over the
  schema dispatch — pre-schema legacy that predates the yaml
  abstraction. bash/zsh were lying about this (`direct` in yaml
  vs tempfile at runtime) until `e5fec38` corrected them. pwsh's
  hardcoded path is genuinely more complex than the schema can
  express today (cursor-wrap-aware echo via
  `BuildMultiLineTempfileBody`) — generalising it needs new
  schema surface (`cursor_wrap_aware_echo_template` or similar)
  that isn't worth adding until there's a second shell with the
  same need. Documented here for future-me so the asymmetry isn't
  mysterious.
- **ccl / abcl top-level `?` prompt leaked into output**
  (`ad9d010`). `CommandTracker.CleanDelta`'s trailing-prompt
  suppressor (`IsShellPrompt`) recognised `$ # % > ❯ λ` terminators
  but not `?`, so the literal `? ` that both Common Lisp adapters'
  integration hooks emit after OSC A ended up tacked onto every
  AI-visible command output (`2\n?` instead of `2` for `(+ 1 1)`).
  Added `line.EndsWith('?')` to the chain. Only affects the
  post-primary drain cleaner — command output itself goes through
  `CleanOutput` which uses a separate code path. Nested
  break-loop prompts `1 > ` / `2 > ` already matched via `>` so
  the hit was specifically the top-level `? ` case.
- **`NormalizeShellFamily` stays.** It looks like a hardcoded
  shell-family helper but it's the path-to-registry-key normaliser
  that `AdapterRegistry.Find` itself uses as a lookup key —
  `Path.GetFileNameWithoutExtension(shell).ToLowerInvariant()`. The
  last *real* shell-family literal in `ConsoleManager`
  (`IsWindowsNativeShellFamily`) was replaced by `cwd_format` in
  commit `7dfb533`.

---

## Commit history at a glance

Phase B → C → C+ → regex-strategy → cache-on-busy-receive → ABCL →
polish round → 0.8.0 debugger round → zsh recovery round arc is
~60 commits, each a self-contained story. Newest first:

```
e5fec38  docs(adapters): correct bash/zsh multiline_delivery to match runtime
5fe6a6f  refactor(schema): remove dead init.inject block and its model classes
dd494b2  refactor(schema): generalize zsh ZDOTDIR fix as init.delivery: rc_file
0b88b28  feat(bash): prefer Git Bash / MSYS2 over WSL bash via executable_candidates
f8e721d  feat(worker): stage integration.zsh via ZDOTDIR on Windows
ad9d010  fix(tracker): drop ?-terminated prompts from post-primary delta
828c159  docs(changelog): clarify clear_line vs hold gate coverage
c7ea05e  docs(changelog): document pre-session ?-terminated prompt fix in 0.8.0
68b18ef  docs(changelog): correct embedded-adapter count to 18
59074e0  docs(readme): reflect 0.8.0 adapter inventory
eca302e  chore(release): 0.8.0 — debugger adapter framework + 3 new REPLs
3576cec  feat(adapters): add deno REPL adapter
f6c5819  feat(adapters): add lua REPL adapter
f285542  feat(adapters): add sqlite3 REPL adapter
9e73dac  fix(launcher): hide console window for --adapter-tests workers
f2f3834  fix(worker): initialize ModeMatch with explicit nulls, not default
8fd9264  feat(adapters): add pdb debugger adapter
686a14b  fix(tests): suppress user input in adapter test workers
bb7e4e3  fix(worker): hold user input during AI command execution
0a98d31  feat(adapters): debugger adapter framework — perldb + jdb
ca78f95  fix(worker): run ModeDetector against raw ring bytes so post-OSC-A prompts match
feac057  test(adapters): pin input.clear_line per-adapter expectations
c90d3f1  feat(worker): stop console focus theft + opt-in line-editor flush before execute
39b6a83  fix(worker): nested datum comments stop leaking pending counter through inner atoms
737f0a3  fix(worker): buffer split-chunk OSC title sequences to stop shell titles leaking
09b71f2  test(worker): cover ReplaceOscTitle against shell-emitted OSC 0/1/2
f19f3c1  fix(adapters): correct node/groovy signals.interrupt declarations after live verification
3e84a22  docs: reflect ABCL adapter round in HANDOFF + CHANGELOG
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
061bb42  feat(python): multi-line command delivery via _ripple_exec_file tempfile
529dff6  feat(adapters): load external YAMLs from ~/.ripple/adapters
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
live-verified via the ripple-dev hot-reload loop (sitter_kill →
Build.ps1 → lazy respawn → actually run the new feature).
