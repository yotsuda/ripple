using Ripple.Services;

namespace Ripple.Tests;

/// <summary>
/// Unit tests for CommandTracker.
///
/// Post-refactor the tracker is finalization-free: it only tracks Busy
/// state, accumulates the recent-output ring, captures AI-command output
/// into a <see cref="CommandOutputCapture"/>, and emits a
/// <see cref="CompletedCommandSnapshot"/> once OSC C/D/A close a cycle.
/// Cleaning, truncation, echo-stripping, StatusLine baking, and cache
/// storage all live in ConsoleWorker now (see ConsoleWorkerTests).
///
/// These tests therefore exercise:
///   - Busy semantics (user OSC, AI command, polling hint)
///   - Recent-output ring VT-lite behaviour
///   - Snapshot production: OSC boundary capture, flip-to-cache, preempt
/// </summary>
public class CommandTrackerTests
{
    public static void Run()
    {
        var pass = 0;
        var fail = 0;

        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== CommandTracker Tests ===");

        static OscParser.OscEvent Evt(OscParser.OscEventType t, int exit = 0, string? cwd = null)
            => new(t, exit, cwd);

        // Helper: build a minimally-populated CommandRegistration. All
        // per-adapter context (shell family, echo strategy, display name)
        // is passed through verbatim to the emitted snapshot, so most
        // tests can leave them null.
        static CommandTracker.CommandRegistration Reg(
            string command,
            int timeoutMs = 30_000,
            string? shellFamily = null,
            string? displayName = null,
            string? inputEchoStrategy = null)
            => new(
                CommandText: command,
                PtyPayload: command + "\r",
                InputEchoLineEnding: "\r",
                InputEchoStrategy: inputEchoStrategy,
                ShellFamily: shellFamily,
                DisplayName: displayName,
                PostPromptSettleMs: 150,
                TimeoutMs: timeoutMs);

        // Helper: subscribe to SnapshotProduced and return a holder that
        // the test can query after driving OSC events. The tracker emits
        // OUTSIDE its lock so synchronous assignment is safe.
        static (Func<CompletedCommandSnapshot?> GetLast, Action Reset) CaptureSnapshot(CommandTracker t)
        {
            CompletedCommandSnapshot? last = null;
            t.SnapshotProduced += s => last = s;
            return (() => last, () => last = null);
        }

        // Helper: read the cleaned command-window output out of a snapshot.
        // Mirrors what ConsoleWorker.FinalizeSnapshotAsync does for the
        // happy path (no echo-strip, no truncation).
        static string Cleaned(CompletedCommandSnapshot s)
            => CommandOutputFinalizer.Clean(s.Capture, s.CommandStart, s.CommandEnd);

        // Test 1: fresh tracker is idle.
        {
            var t = new CommandTracker();
            Assert(!t.Busy, "initial Busy is false");
        }

        // Helper: simulate shell startup (initial OSC B then first OSC A) so
        // subsequent OSC B/C events are recognised as user commands. The
        // tracker gates user-busy tracking until the shell has reached its
        // first prompt, otherwise the startup OSC B would leave new consoles
        // looking busy.
        static void PrimeShellReady(CommandTracker t)
        {
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.CommandInputStart));
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.PromptStart));
        }

        // Test 2: pwsh user-command pattern — OSC B on Enter, OSC A after prompt.
        {
            var t = new CommandTracker();
            PrimeShellReady(t);
            Assert(!t.Busy, "pwsh: idle after startup B → A");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            Assert(t.Busy, "pwsh: Busy true after OSC B (Enter)");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            Assert(t.Busy, "pwsh: still Busy after OSC C (fires from prompt fn after cmd)");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "pwsh: still Busy after OSC D");

            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "C:\\Users"));
            Assert(t.Busy, "pwsh: still Busy after OSC P");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(!t.Busy, "pwsh: Busy cleared by OSC A");
        }

        // Test 3: bash/zsh user-command pattern — OSC C on preexec, OSC D+A on precmd.
        {
            var t = new CommandTracker();
            PrimeShellReady(t);
            Assert(!t.Busy, "bash: idle after startup B → A");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            Assert(t.Busy, "bash: Busy true after OSC C (preexec)");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "bash: still Busy after OSC D");

            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "/home/user"));
            Assert(t.Busy, "bash: still Busy after OSC P");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(!t.Busy, "bash: Busy cleared by OSC A");
        }

        // Test 4: startup OSC B must NOT mark busy before the shell is ready.
        // Before the first OSC A there is no established baseline, and the
        // initial OSC B from integration scripts would otherwise race against
        // the first execute_command from the proxy.
        {
            var t = new CommandTracker();
            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            Assert(!t.Busy, "startup: initial OSC B is ignored (shell not ready)");
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(!t.Busy, "startup: still idle after first OSC A");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            Assert(t.Busy, "startup: OSC B after first prompt does set busy");
        }

        // Test 5: LastKnownCwd is updated even for user-command OSC P.
        {
            var t = new CommandTracker();
            Assert(t.LastKnownCwd == null, "LastKnownCwd starts null");

            PrimeShellReady(t);
            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "/tmp"));
            Assert(t.LastKnownCwd == "/tmp", "LastKnownCwd updated from user OSC P");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(t.LastKnownCwd == "/tmp", "LastKnownCwd retained after prompt");
        }

        // Test 6: AI command wins over user state — RegisterCommand while idle, then events
        // still reflect Busy via _isAiCommand until the AI command resolves.
        {
            var t = new CommandTracker();
            var task = t.RegisterCommand(Reg("Get-Date"));
            Assert(t.Busy, "AI cmd: Busy true after RegisterCommand");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "AI cmd: still Busy before prompt resolves");
        }

        // Test 7: RegisterCommand refuses when another AI command is in flight.
        {
            var t = new CommandTracker();
            _ = t.RegisterCommand(Reg("first"));
            bool threw = false;
            try { _ = t.RegisterCommand(Reg("second")); }
            catch (InvalidOperationException) { threw = true; }
            Assert(threw, "AI cmd: second RegisterCommand throws");
        }

        // Test 8: a late first-prompt OSC A that arrives AFTER RegisterCommand
        // but BEFORE the command's own OSC C / OSC D boundary must NOT resolve
        // the pending task. Reproduces the "slow pwsh startup + timeout
        // fallback" case where the pre-command buffer (reason banner etc.)
        // would otherwise be returned as the command's output.
        {
            var t = new CommandTracker();
            var (getLast, _) = CaptureSnapshot(t);
            var task = t.RegisterCommand(Reg("Get-Location"));

            // Simulate the bytes that arrived between worker-ready and the
            // real first prompt: the Write-Host reason text + the first
            // prompt's OSC sequence arriving AFTER RegisterCommand has
            // already fired. OSC B from integration.ps1's init-time write,
            // then the delayed first prompt's OSC D/P/A.
            t.FeedOutput("Reason: test\n\n");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandInputStart));
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "C:\\Users\\foo"));
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));

            Assert(!task.IsCompleted, "late first-prompt OSC A does NOT resolve before OSC C");
            Assert(getLast() == null, "late first-prompt OSC A does NOT emit a snapshot");
            Assert(t.Busy, "AI cmd: still Busy after stray first prompt");

            // Now simulate the real command actually running, which will
            // deliver OSC C + output + OSC D + OSC A in proper order.
            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            t.FeedOutput("C:\\MyProj\\rippleshell\n");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            t.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: "C:\\MyProj\\rippleshell"));
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));

            Assert(task.IsCompletedSuccessfully, "real OSC C/D/A resolves the task");
            var snap = task.Result;
            var output = Cleaned(snap);
            Assert(!output.Contains("Reason"), "reason banner is NOT in captured output");
            Assert(output.Contains("C:\\MyProj\\rippleshell"), "real command output is captured");
            Assert(snap.Cwd == "C:\\MyProj\\rippleshell", "post-command cwd is reported");
        }

        // Test 9: recent-output ring buffer captures output from AI commands,
        // user commands, and pre-prompt boot noise alike — regardless of
        // whether an AI command is registered. peek_console / timeout
        // partialOutput depend on this "always on" behaviour.
        {
            var t = new CommandTracker();
            Assert(t.GetRecentOutputSnapshot() == "", "recent: empty on fresh tracker");

            // Pre-prompt boot bytes — no AI command active yet.
            t.FeedOutput("boot banner\n");
            Assert(t.GetRecentOutputSnapshot().Contains("boot banner"), "recent: pre-prompt output captured");

            PrimeShellReady(t);
            t.FeedOutput("user typed output\n");
            Assert(t.GetRecentOutputSnapshot().Contains("user typed output"), "recent: user output captured");

            // AI command: real command output flows through too.
            _ = t.RegisterCommand(Reg("Get-Date"));
            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            t.FeedOutput("ai command output\n");
            var snapshot = t.GetRecentOutputSnapshot();
            Assert(snapshot.Contains("ai command output"), "recent: AI output captured");
        }

        // Test 10: ring buffer wraps correctly when output exceeds capacity.
        // A 4 KB write followed by a marker tail should show only the
        // marker plus the tail of the prior content, never the head.
        {
            var t = new CommandTracker();
            var big = new string('x', 5000);
            t.FeedOutput(big);
            t.FeedOutput("<<TAIL>>");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap.EndsWith("<<TAIL>>"), "recent: tail marker is at the end after wrap");
            Assert(snap.Length <= 4096, "recent: snapshot bounded by ring capacity");
            Assert(!snap.Contains("<<HEAD>>"), "recent: head is not present");
        }

        // Test 11: GetRecentOutputSnapshot strips ANSI but preserves CRLF
        // normalisation (matches AI command output cleaning).
        {
            var t = new CommandTracker();
            t.FeedOutput("line1\r\n\x1b[2Kline2\r\n");
            var snap = t.GetRecentOutputSnapshot();
            Assert(!snap.Contains('\r'), "recent: CR stripped");
            Assert(!snap.Contains('\x1b'), "recent: ANSI stripped");
            Assert(snap.Contains("line1\nline2"), "recent: LF-joined content preserved");
        }

        // Test 12: CR-overwrite collapse — PSReadLine's typing animation
        // and progress bars use bare \r to redraw in place. The snapshot
        // must show only the final state of each line, not the historical
        // concatenation of every intermediate redraw.
        {
            var t = new CommandTracker();
            // Simulate a progress bar updating in place, then a real line.
            t.FeedOutput("Progress 10%\rProgress 50%\rProgress 100%\ndone\n");
            var snap = t.GetRecentOutputSnapshot();
            Assert(!snap.Contains("Progress 10%"), "recent: earlier progress state dropped");
            Assert(!snap.Contains("Progress 50%"), "recent: intermediate progress state dropped");
            Assert(snap.Contains("Progress 100%"), "recent: final progress state kept");
            Assert(snap.Contains("done"), "recent: following line kept");
        }

        // Test 13: CRLF is preserved — CR collapse must not eat the prior
        // line when it ends with CRLF (regression guard).
        {
            var t = new CommandTracker();
            t.FeedOutput("first\r\nsecond\r\n");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap.Contains("first"), "recent: CRLF prior line preserved");
            Assert(snap.Contains("second"), "recent: CRLF second line preserved");
        }

        // Test 14: PSReadLine-style in-place redraw using CSI cursor
        // positioning rather than \r. CHA (\e[G) rewinds to column 1,
        // EL (\e[K) erases to end of line. The final state should
        // collapse to just the last rewrite, not the accumulated history.
        {
            var t = new CommandTracker();
            t.FeedOutput("PS> abc\x1b[G\x1b[KPS> abcdef\x1b[G\x1b[KPS> final");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "PS> final", $"recent: CSI redraws collapse to final — got: {snap}");
        }

        // Test 15: CUP (\e[<row>;<col>H) positioning is collapsed into
        // column-only movement so in-place row rewrites still work.
        {
            var t = new CommandTracker();
            t.FeedOutput("first line\x1b[1;1Hoverwritten");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "overwritten", $"recent: CUP to col 1 overwrites — got: {snap}");
        }

        // Test 16: SGR sequences are dropped cleanly (no leftover bytes).
        {
            var t = new CommandTracker();
            t.FeedOutput("\x1b[31mred\x1b[0m text");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "red text", $"recent: SGR dropped — got: {snap}");
        }

        // Test 17: OSC sequences terminated by BEL or ST are dropped.
        // Use \a (0x07) instead of \x07 — \x hex escapes are greedy and
        // would gobble the following 'a' as part of the hex literal.
        {
            var t = new CommandTracker();
            t.FeedOutput("before\x1b]0;window title\aafter");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "beforeafter", $"recent: OSC BEL dropped — got: {snap}");
        }

        // Test 18: cursor back (\e[D) and forward (\e[C) adjust the
        // write position on the current line.
        {
            var t = new CommandTracker();
            t.FeedOutput("abcdef\x1b[3DXY");
            // start: "abcdef" col=6, \e[3D → col=3, write "X" at 3 → "abcXef" col=4, write "Y" at 4 → "abcXYf" col=5
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "abcXYf", $"recent: cursor back then write — got: {snap}");
        }

        // Test 19: multi-row CUP addresses absolute rows. A new row's
        // contents must NOT bleed into a previous row. Regression for
        // the ConPTY "stale tail" bug where CUP(2,1) to start a new
        // command line was collapsed onto row 1 and left trailing
        // chars from the previous command.
        {
            var t = new CommandTracker();
            t.FeedOutput("long first row content\x1b[2;1Hshort");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "long first row content\nshort",
                $"recent: CUP(2,1) writes to row 2, row 1 preserved — got: {snap}");
        }

        // Test 20: CUP to an existing row overwrites from col 1
        // without touching unrelated cells past the new content.
        // Uses \r\n (CRLF) matching real PTY output — bare \n
        // only moves down without resetting col per VT spec.
        {
            var t = new CommandTracker();
            t.FeedOutput("aaaaaaaaaaaa\r\nrow2\x1b[1;1Hbbb\x1b[K");
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "bbb\nrow2",
                $"recent: CUP(1,1) + shorter write + EL clears tail — got: {snap}");
        }

        // Test 21: CUU (cursor up) moves the cursor to the previous
        // row so subsequent writes land on that row.
        {
            var t = new CommandTracker();
            t.FeedOutput("row1\r\nrow2\r\nrow3\x1b[2AX");
            // 3 rows written via CRLF. After "row3", Col=4.
            // CUU 2 → Row goes from 2 to 0, Col stays at 4.
            // Write 'X' at row 0 col 4 → "row1X".
            var snap = t.GetRecentOutputSnapshot();
            Assert(snap == "row1X\nrow2\nrow3",
                $"recent: CUU then write — got: {snap}");
        }

        // Test: \e[<params>t (DEC window manipulation) is treated as a
        // full-screen refresh trigger. ConPTY emits this right before
        // repainting the entire viewport, so VtLite should drop the
        // current grid and start fresh on the refresh content.
        {
            var t = new CommandTracker();
            // Old stale content, then the window-manip refresh trigger,
            // then a fresh screen write.
            t.FeedOutput("stale junk line\x1b[8;25;105tfresh content\nsecond line");
            var snap = t.GetRecentOutputSnapshot();
            Assert(!snap.Contains("stale junk"),
                $"recent: \\e[<...>t clears stale pre-refresh content — got: {snap}");
            Assert(snap.Contains("fresh content"),
                $"recent: post-refresh content is captured — got: {snap}");
            Assert(snap.Contains("second line"),
                $"recent: post-refresh multi-row content — got: {snap}");
        }

        // Test 22: the recent-output ring is cleared on (a) the FIRST
        // OSC A (PromptStart) — drops pre-shell boot noise and any
        // prior-session residue on a reused standby console — and (b)
        // every OSC C (CommandExecuted) — drops PSReadLine typing
        // noise and inline prediction artifacts that are rendered via
        // terminal-absolute coordinates we can't faithfully reflow.
        // After OSC C, the ring accumulates only the actual command
        // output, giving peek_console a clean "what is the current
        // command doing right now?" view.
        {
            var t = new CommandTracker();

            // Stale residue from a prior shell session (reused standby).
            t.FeedOutput("stale output from a previous session\n");
            Assert(t.GetRecentOutputSnapshot().Contains("stale output"),
                "recent: stale residue present before first PromptStart");

            // First OSC A = shell is ready. Ring gets wiped.
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.PromptStart));
            Assert(t.GetRecentOutputSnapshot() == "",
                "recent: first OSC A clears the ring");

            // User starts typing — PSReadLine rendering noise flows in.
            t.FeedOutput("PS > \x1b[?25l\x1b[93mS\x1b[97m\x1b[2met-Location prediction\x1b[?25h");
            Assert(t.GetRecentOutputSnapshot().Length > 0,
                "recent: typing noise is in the ring pre-OSC-C");

            // OSC C fires when the command actually starts running.
            // Ring must clear to drop the typing noise.
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.CommandExecuted));
            Assert(t.GetRecentOutputSnapshot() == "",
                "recent: OSC C clears PSReadLine typing noise");

            // Command output accumulates into the fresh ring.
            t.FeedOutput("actual command output\n");
            Assert(t.GetRecentOutputSnapshot().Contains("actual command output"),
                "recent: post-OSC-C output captured cleanly");

            // Command finishes, next prompt fires. The ring is NOT
            // cleared here — the current command's output stays
            // visible until the NEXT command starts.
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.CommandFinished, ExitCode: 0));
            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.PromptStart));
            var afterPrompt = t.GetRecentOutputSnapshot();
            Assert(afterPrompt.Contains("actual command output"),
                $"recent: OSC A after first cycle preserves output — got: {afterPrompt}");
        }

        // SetUserBusyHint — polling-based busy reporting for cmd.
        // The tracker's Busy property must OR the polling hint with the OSC
        // signals so the cmd worker can flip the state from its CPU/child
        // sampler without depending on shell integration.
        {
            var t = new CommandTracker();
            PrimeShellReady(t);
            Assert(!t.Busy, "polling hint: idle by default");

            t.SetUserBusyHint(true);
            Assert(t.Busy, "polling hint: true makes Busy true");

            t.SetUserBusyHint(false);
            Assert(!t.Busy, "polling hint: false makes Busy false again");
        }

        // SetUserBusyHint must be a no-op while an AI command is in flight,
        // because the AI command has its own _isAiCommand busy state and the
        // poll loop might race a "not busy" reading right as a fresh AI cmd
        // is registered.
        {
            var t = new CommandTracker();
            PrimeShellReady(t);
            _ = t.RegisterCommand(Reg("Get-Date"));
            Assert(t.Busy, "polling hint vs AI: Busy from AI command");

            t.SetUserBusyHint(false);
            Assert(t.Busy, "polling hint: false ignored while AI command active");
        }

        // OSC user-busy and polling hint coexist — both signals can mark
        // busy independently and clearing one doesn't clear the other.
        {
            var t = new CommandTracker();
            PrimeShellReady(t);

            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.CommandExecuted));
            Assert(t.Busy, "polling+osc: OSC C set busy");

            t.SetUserBusyHint(true);
            Assert(t.Busy, "polling+osc: both signals busy");

            t.HandleEvent(new OscParser.OscEvent(OscParser.OscEventType.PromptStart));
            Assert(t.Busy, "polling+osc: OSC A clears OSC busy but polling still busy");

            t.SetUserBusyHint(false);
            Assert(!t.Busy, "polling+osc: clearing both finally clears Busy");
        }

        // ================================================================
        // Snapshot-production tests — exercise the boundary the tracker
        // owns post-refactor. The worker is responsible for cleaning /
        // truncating / StatusLine / cache; those are covered in
        // ConsoleWorkerTests. Here we verify:
        //   - Normal completion emits a snapshot AND completes the task
        //   - FlipToCacheMode detaches the task but still emits the
        //     eventual snapshot through SnapshotProduced
        //   - Preemptive timeout produces the same detach + eventual
        //     snapshot shape as explicit flip
        //   - Snapshot carries the exact registration metadata the
        //     finalize-once path needs (echo strategy, ptyPayload,
        //     shell family, display name)
        // ================================================================

        // Helper: drive an AI command through OSC C → output → OSC D → OSC A.
        // Mirrors the sequence a real shell's integration script emits.
        static void DriveCycle(CommandTracker tracker, string output, int exit = 0, string? cwd = null)
        {
            tracker.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            if (!string.IsNullOrEmpty(output))
                tracker.FeedOutput(output);
            tracker.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: exit));
            if (cwd != null)
                tracker.HandleEvent(Evt(OscParser.OscEventType.Cwd, cwd: cwd));
            tracker.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
        }

        // Normal completion: task completes with the snapshot AND the
        // SnapshotProduced event fires with the same instance. Worker
        // subscribes to the event; inline await uses the task. Both paths
        // must see identical data.
        //
        // The tracker intentionally stays Busy through snapshot emission
        // — Busy only clears once the worker's finalize-once path calls
        // ReleaseAiCommand after the result has been delivered inline
        // or appended to the cache. That closes the settle-window race
        // where a fast-polling client could otherwise see
        // {status: standby, hasCachedOutput: false} between the
        // snapshot firing and the cache entry landing.
        {
            var t = new CommandTracker();
            var (getLast, _) = CaptureSnapshot(t);
            var task = t.RegisterCommand(Reg("Get-Date"));
            DriveCycle(t, "normal-output\n", exit: 0, cwd: "/home");

            Assert(task.IsCompletedSuccessfully, "normal: task completed successfully");
            var snap = task.Result;
            Assert(snap.Command == "Get-Date", "normal: snapshot carries command text");
            Assert(snap.ExitCode == 0, "normal: snapshot carries exit code");
            Assert(snap.Cwd == "/home", "normal: snapshot carries cwd");
            Assert(Cleaned(snap).Contains("normal-output"), "normal: capture contains output");
            Assert(t.Busy, "normal: tracker stays Busy until worker releases (settle-window race guard)");

            t.ReleaseAiCommand(snap.Generation);
            Assert(!t.Busy, "normal: tracker idle after ReleaseAiCommand");

            var emitted = getLast();
            Assert(emitted != null, "normal: SnapshotProduced fired");
            Assert(ReferenceEquals(emitted, snap), "normal: event & task snapshot are same instance");
        }

        // FlipToCacheMode on an idle tracker is a safe no-op — matters for
        // the "any tool arrives" trigger that fires regardless of state.
        {
            var t = new CommandTracker();
            t.FlipToCacheMode();
            Assert(!t.Busy, "flip: idle no-op leaves Busy=false");
        }

        // Mid-command FlipToCacheMode: task faults with TimeoutException,
        // the tracker stays busy (shell hasn't finished), and when the
        // eventual OSC cycle fires the snapshot flows through
        // SnapshotProduced (the worker's fallback delivery path).
        {
            var t = new CommandTracker();
            var (getLast, _) = CaptureSnapshot(t);
            var task = t.RegisterCommand(Reg("Get-Date"));
            Assert(!task.IsCompleted, "flip: task pending before flip");

            t.FlipToCacheMode();
            Assert(task.IsFaulted, "flip: task faulted after FlipToCacheMode");
            Assert(task.Exception!.InnerException is TimeoutException,
                "flip: task fault is TimeoutException");
            Assert(t.Busy, "flip: tracker still Busy — command still running in shell");
            Assert(getLast() == null, "flip: no snapshot yet (shell hasn't finished)");

            // Shell eventually emits the command cycle. The TCS is detached,
            // so the snapshot only shows up on the event.
            DriveCycle(t, "output-1\n", exit: 0, cwd: "/tmp");
            Assert(t.Busy, "flip: tracker stays Busy until worker releases");

            var snap = getLast();
            Assert(snap != null, "flip: snapshot delivered via event after cycle");
            Assert(Cleaned(snap!).Contains("output-1"), "flip: snapshot has command output");
            Assert(snap!.ExitCode == 0, "flip: snapshot exit code preserved");
            Assert(snap.Cwd == "/tmp", "flip: snapshot cwd preserved");
            Assert(snap.Command == "Get-Date", "flip: snapshot command text preserved");

            t.ReleaseAiCommand(snap.Generation);
            Assert(!t.Busy, "flip: tracker idle after ReleaseAiCommand");
        }

        // Sequential flipped commands emit separate snapshots in order.
        // Worker appends each to its _cachedResults; here we just verify
        // the tracker produces distinct snapshots with the right payloads.
        // Each DriveCycle leaves Busy set until ReleaseAiCommand fires,
        // so the test simulates the worker's finalize-once path by
        // releasing between commands.
        {
            var t = new CommandTracker();
            var snapshots = new List<CompletedCommandSnapshot>();
            t.SnapshotProduced += s => snapshots.Add(s);

            _ = t.RegisterCommand(Reg("first"));
            t.FlipToCacheMode();
            DriveCycle(t, "first-output\n", exit: 0);
            t.ReleaseAiCommand(snapshots[0].Generation);

            _ = t.RegisterCommand(Reg("second"));
            t.FlipToCacheMode();
            DriveCycle(t, "second-output\n", exit: 1);
            t.ReleaseAiCommand(snapshots[1].Generation);

            Assert(snapshots.Count == 2, $"multi: two snapshots emitted (got {snapshots.Count})");
            Assert(snapshots[0].Command == "first", "multi: first command in order");
            Assert(Cleaned(snapshots[0]).Contains("first-output"), "multi: first output preserved");
            Assert(snapshots[1].Command == "second", "multi: second command in order");
            Assert(Cleaned(snapshots[1]).Contains("second-output"), "multi: second output preserved");
            Assert(snapshots[1].ExitCode == 1, "multi: second non-zero exit preserved");
        }

        // Registration metadata flows into the snapshot verbatim — shell
        // family, display name, echo strategy, and PTY payload baseline
        // are all needed by the worker's finalize-once path (echo strip,
        // StatusLine baking).
        {
            var t = new CommandTracker();
            var (getLast, _) = CaptureSnapshot(t);
            var reg = new CommandTracker.CommandRegistration(
                CommandText: "dir",
                PtyPayload: "dir\r",
                InputEchoLineEnding: "\r",
                InputEchoStrategy: "deterministic_byte_match",
                ShellFamily: "cmd",
                DisplayName: "Catfish",
                PostPromptSettleMs: 200,
                TimeoutMs: 30_000);
            _ = t.RegisterCommand(reg);
            DriveCycle(t, "Volume in drive C\n", exit: 0);

            var snap = getLast();
            Assert(snap != null, "metadata: snapshot emitted");
            Assert(snap!.ShellFamily == "cmd", "metadata: shell family carried");
            Assert(snap.DisplayName == "Catfish", "metadata: display name carried");
            Assert(snap.InputEchoStrategy == "deterministic_byte_match",
                "metadata: echo strategy carried");
            Assert(snap.PtyPayloadBaseline == "dir\r", "metadata: pty payload baseline carried");
            Assert(snap.InputEchoLineEnding == "\r", "metadata: line ending carried");
            Assert(snap.PostPromptSettleMs == 200, "metadata: settle ms carried");
        }

        // 170s preemptive cap — RegisterCommand with a huge timeoutMs must
        // cap internally and not immediately fault. The exact capped value
        // isn't directly observable but the call must succeed and the task
        // must stay pending.
        {
            var t = new CommandTracker();
            var task = t.RegisterCommand(Reg("long", timeoutMs: 3_600_000)); // 1 hour
            Assert(t.Busy, "cap: Busy after RegisterCommand with large timeout");
            Assert(!task.IsCompleted, "cap: task not completed immediately");
            DriveCycle(t, "ok\n", exit: 0);
            Assert(task.IsCompletedSuccessfully, "cap: task resolves normally via OSC");
        }

        // Post-OSC-A trailing bytes still reach the capture the finalizer
        // slices. Regression guard for the Codex-flagged bug where
        // BuildAndReleaseSnapshot called Cleanup() synchronously, nulling
        // _capture before the worker's WaitCaptureStable loop could observe
        // real length growth from trailing rows (Format-Table tails, cmd
        // PROMPT repaint, bash progress-bar final frames) emitted between
        // OSC A and the settle deadline. The fix: hand _capture off to
        // _settlingCapture in BuildAndReleaseSnapshot so FeedOutput can
        // keep routing post-OSC-A bytes into the SAME capture object that
        // the snapshot references. The worker owns detach via
        // DetachSettlingCapture once it has read its final slice.
        {
            var t = new CommandTracker();
            var (getLast, _) = CaptureSnapshot(t);
            _ = t.RegisterCommand(Reg("trailing"));
            DriveCycle(t, "main-line\n", exit: 0);

            var snap = getLast();
            Assert(snap != null, "post-osc-a: snapshot emitted");

            // OSC A has already fired and the tracker has called
            // BuildAndReleaseSnapshot. In the old code _capture would
            // be null, so _settlingCapture would also be null, and
            // these FeedOutput calls would be discarded silently.
            var lengthAfterSnapshot = snap!.Capture.Length;
            t.FeedOutput("trailing-row-after-osc-a\n");
            var lengthAfterTrail = snap.Capture.Length;

            Assert(lengthAfterTrail > lengthAfterSnapshot,
                $"post-osc-a: capture length grew after trailing FeedOutput (before={lengthAfterSnapshot}, after={lengthAfterTrail})");

            // Read the trailing region from the SAME capture object the
            // snapshot owns. This is exactly the read shape
            // ConsoleWorker.FinalizeSnapshotAsync uses after WaitCaptureStable
            // observes growth — slice from CommandStart to the current
            // Length, not from CommandStart to snap.CommandEnd (which was
            // set at OSC D and pre-dates the trailing bytes).
            var extended = snap.Capture.ReadSlice(snap.CommandStart, snap.Capture.Length - snap.CommandStart);
            Assert(extended.Contains("main-line"),
                "post-osc-a: main command output still present in extended slice");
            Assert(extended.Contains("trailing-row-after-osc-a"),
                "post-osc-a: trailing bytes arrived in the same capture");

            // Worker's final handoff: once it has read its slice, it
            // cuts the tracker's write path so further PTY bytes don't
            // try to append to a disposed capture. Post-detach
            // FeedOutput must not extend the handed-off capture.
            t.DetachSettlingCapture(snap.Capture);
            var lengthBeforeOrphan = snap.Capture.Length;
            t.FeedOutput("post-detach bytes\n");
            Assert(snap.Capture.Length == lengthBeforeOrphan,
                "post-osc-a: after detach, further FeedOutput does NOT extend the capture");

            // DetachSettlingCapture with a stale capture (already replaced
            // by a newer RegisterCommand) must be a safe no-op.
            var orphaned = new CommandOutputCapture();
            t.DetachSettlingCapture(orphaned);  // should not throw
            orphaned.Complete();
        }

        // Back-to-back commands: a new RegisterCommand arriving before the
        // worker finishes the previous finalize must displace the stale
        // settling capture so the new command gets exclusive routing.
        // Trailing bytes for the prior command land nowhere (the worker's
        // slice has already been read by then), which is the correct
        // tradeoff — the new command's bytes must not bleed into the old
        // capture.
        {
            var t = new CommandTracker();
            var (getLast, reset) = CaptureSnapshot(t);
            _ = t.RegisterCommand(Reg("first"));
            DriveCycle(t, "first-output\n", exit: 0);
            var firstSnap = getLast();
            Assert(firstSnap != null, "back-to-back: first snapshot emitted");

            // No DetachSettlingCapture yet — simulate a racing new
            // RegisterCommand before the worker finishes finalize.
            reset();
            _ = t.RegisterCommand(Reg("second"));
            DriveCycle(t, "belongs to second\n", exit: 0);
            var secondSnap = getLast();
            Assert(secondSnap != null, "back-to-back: second snapshot emitted");
            Assert(!ReferenceEquals(firstSnap!.Capture, secondSnap!.Capture),
                "back-to-back: second snapshot uses a fresh capture");
            Assert(Cleaned(secondSnap).Contains("belongs to second"),
                "back-to-back: second capture owns its own bytes");
            // The first capture's bytes must NOT bleed into the second
            // capture — the tracker cut the write path to the first
            // capture at RegisterCommand("second") time.
            Assert(!Cleaned(secondSnap).Contains("first-output"),
                "back-to-back: first capture's bytes do NOT leak into second");
        }

        // Preemptive timeout fires FlipToCacheMode through the CTS
        // registration, the task faults with TimeoutException, and the
        // eventual OSC cycle emits the snapshot through the event. Uses a
        // 1s timeout so the total test wait is bounded.
        {
            var t = new CommandTracker();
            var (getLast, _) = CaptureSnapshot(t);
            var task = t.RegisterCommand(Reg("slow", timeoutMs: 1000));

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && !task.IsCompleted)
                System.Threading.Thread.Sleep(50);

            Assert(task.IsFaulted, "preempt: task faulted after 1s timeout");
            Assert(task.Exception!.InnerException is TimeoutException,
                "preempt: fault is TimeoutException");
            Assert(t.Busy, "preempt: tracker still busy (command still running in shell)");
            Assert(getLast() == null, "preempt: no snapshot yet (shell hasn't finished)");

            DriveCycle(t, "slow-output\n", exit: 0);
            Assert(t.Busy, "preempt: tracker stays Busy until worker releases");

            var snap = getLast();
            Assert(snap != null, "preempt: snapshot delivered via event after cycle");
            Assert(Cleaned(snap!).Contains("slow-output"),
                "preempt: output captured via event path");
            Assert(snap!.Command == "slow", "preempt: command text preserved");

            t.ReleaseAiCommand(snap.Generation);
            Assert(!t.Busy, "preempt: tracker idle after ReleaseAiCommand");
        }

        // Settle-window race guard (Codex Bug 1): the tracker must
        // stay Busy across the interval between snapshot emission and
        // the worker's ReleaseAiCommand, because get_status's
        // "standby + hasCachedOutput=false" response during that
        // window causes ConsoleManager.WaitForCompletionAsync to drop
        // the pid as "cache lost". We simulate a fast poller that
        // queries Busy immediately after each OSC event: at no point
        // between RegisterCommand and ReleaseAiCommand should Busy
        // read false.
        {
            var t = new CommandTracker();
            var (getLast, _) = CaptureSnapshot(t);
            _ = t.RegisterCommand(Reg("race"));
            Assert(t.Busy, "race: Busy set by RegisterCommand");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            Assert(t.Busy, "race: Busy after OSC C");

            t.FeedOutput("race-output\n");
            Assert(t.Busy, "race: Busy after output");

            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            Assert(t.Busy, "race: Busy after OSC D");

            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            Assert(t.Busy, "race: Busy after OSC A (snapshot emitted, finalize pending)");

            var snap = getLast();
            Assert(snap != null, "race: snapshot emitted");
            // Simulate the worker's settle window: bytes can still
            // arrive between emission and ReleaseAiCommand. Busy must
            // not flicker off at any sampled moment.
            t.FeedOutput("trailing-byte");
            Assert(t.Busy, "race: Busy during settle window");

            t.ReleaseAiCommand(snap!.Generation);
            Assert(!t.Busy, "race: Busy clears only after ReleaseAiCommand");
        }

        // Generation-token guard (Codex Bug 1): a stale
        // ReleaseAiCommand fired by a previous command's finalize
        // AFTER a new command has registered must no-op — the
        // tracker must not clear the new command's busy flag.
        {
            var t = new CommandTracker();
            var (getLast, reset) = CaptureSnapshot(t);
            _ = t.RegisterCommand(Reg("first"));
            DriveCycle(t, "first-output\n", exit: 0);
            var firstSnap = getLast();
            Assert(firstSnap != null, "gen: first snapshot emitted");

            // Simulate a back-to-back RegisterCommand before the
            // worker's finalize-once path had a chance to call
            // ReleaseAiCommand for the first snapshot. Under the new
            // semantics _tcs is null after emission, so the new
            // RegisterCommand is accepted and bumps the generation.
            reset();
            _ = t.RegisterCommand(Reg("second"));
            Assert(t.Busy, "gen: Busy still set after new registration");

            // Stale release from the first finalize now fires. It
            // must NOT clear Busy because the generation differs.
            t.ReleaseAiCommand(firstSnap!.Generation);
            Assert(t.Busy, "gen: stale release from first finalize is a no-op");

            // Complete the second command normally — the fresh
            // generation's release clears Busy as expected.
            DriveCycle(t, "second-output\n", exit: 0);
            var secondSnap = getLast();
            Assert(secondSnap != null, "gen: second snapshot emitted");
            Assert(secondSnap!.Generation != firstSnap.Generation,
                "gen: generations differ between commands");
            t.ReleaseAiCommand(secondSnap.Generation);
            Assert(!t.Busy, "gen: matched release clears Busy");
        }

        // OSC A offset capture (Codex Bug 2): the snapshot must
        // carry the capture length at the moment OSC A fired so the
        // finalizer can cap its read slice before prompt text
        // arrives. Non-pwsh shells stream prompt chars (bash `$ `,
        // cmd PROMPT repaint) immediately after OSC A, and without
        // this cap the "extend past CommandEnd for trailing command
        // output" rule would swallow them into the result.
        {
            var t = new CommandTracker();
            var (getLast, _) = CaptureSnapshot(t);
            _ = t.RegisterCommand(Reg("prompt-bleed"));
            t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
            t.FeedOutput("real-command-output\n");
            t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
            t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            // Simulate bash / cmd streaming the next prompt AFTER
            // OSC A fires — this must NOT bleed into the finalized
            // slice.
            t.FeedOutput("$ ");

            var snap = getLast();
            Assert(snap != null, "prompt-offset: snapshot emitted");
            Assert(snap!.PromptStartOffset.HasValue,
                "prompt-offset: PromptStartOffset is populated on the snapshot");
            Assert(snap.PromptStartOffset < snap.Capture.Length,
                "prompt-offset: post-OSC-A bytes landed in the capture but outside the cap");

            // The finalizer's effectiveEnd = Min(Max(CommandEnd, Length), PromptStartOffset)
            // picks a slice that stops at OSC A, so the cleaned
            // result must not contain the `$ ` prompt bytes.
            var cutoff = snap.PromptStartOffset ?? snap.Capture.Length;
            var effectiveEnd = Math.Min(
                Math.Max(snap.CommandEnd, snap.Capture.Length),
                cutoff);
            var cleaned = CommandOutputFinalizer.Clean(snap.Capture, snap.CommandStart, effectiveEnd);
            Assert(cleaned.Contains("real-command-output"),
                "prompt-offset: cleaned slice contains the real command output");
            Assert(!cleaned.Contains("$ "),
                $"prompt-offset: cleaned slice does NOT contain the post-OSC-A prompt (got: {cleaned})");
        }

        // Codex Review — Bug 2: disposing _timeoutReg must happen
        // outside _lock. The preemptive-timeout callback
        // (FlipToCacheMode) tries to reacquire _lock; with
        // CancellationTokenRegistration.Dispose blocking until that
        // callback finishes, disposing under the lock is a classic
        // AB-BA deadlock. Both dispose sites — AbortPending and the
        // BuildAndReleaseSnapshot emission path — must complete even
        // when the timeout fires concurrently.
        //
        // Drive the race in a background task and bound the worst-
        // case wait: the fixed code finishes in milliseconds, while
        // the pre-fix code would block until the test runner's
        // timeout cancels the process.
        {
            // Abort-pending vs. concurrent preemptive timeout. Fire a
            // short timeout so the CTS is already cancelled (or
            // cancelling) by the time AbortPending takes _lock and
            // tries to dispose _timeoutReg.
            var t = new CommandTracker();
            var task = t.RegisterCommand(Reg("abort-timeout-race", timeoutMs: 1));

            var finished = Task.Run(() =>
            {
                // Spin briefly so the CTS has a chance to start firing
                // FlipToCacheMode on the threadpool while we take the
                // lock inside AbortPending.
                System.Threading.Thread.SpinWait(1000);
                t.AbortPending();
            });
            Assert(finished.Wait(TimeSpan.FromSeconds(5)),
                "timeout-dispose: AbortPending returns even with preemptive-timeout callback racing");
            Assert(task.IsFaulted, "timeout-dispose: pending task faulted after AbortPending");
        }

        {
            // Normal completion vs. concurrent preemptive timeout.
            // BuildAndReleaseSnapshot used to call _timeoutReg.Dispose
            // under _lock; fix the race by disposing outside.
            var t = new CommandTracker();
            var (getLast, _) = CaptureSnapshot(t);
            _ = t.RegisterCommand(Reg("complete-timeout-race", timeoutMs: 1));

            var finished = Task.Run(() =>
            {
                System.Threading.Thread.SpinWait(1000);
                t.HandleEvent(Evt(OscParser.OscEventType.CommandExecuted));
                t.FeedOutput("race-body\n");
                t.HandleEvent(Evt(OscParser.OscEventType.CommandFinished, exit: 0));
                t.HandleEvent(Evt(OscParser.OscEventType.PromptStart));
            });
            Assert(finished.Wait(TimeSpan.FromSeconds(5)),
                "timeout-dispose: snapshot emission returns even with preemptive-timeout callback racing");
            var snap = getLast();
            Assert(snap != null,
                "timeout-dispose: snapshot still emitted after racing completion + timeout");
            if (snap != null) t.ReleaseAiCommand(snap.Generation);
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
