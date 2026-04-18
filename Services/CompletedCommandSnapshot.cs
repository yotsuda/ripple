namespace Ripple.Services;

/// <summary>
/// Lightweight record emitted by <see cref="CommandTracker"/> when an
/// AI command reaches primary completion (OSC A closes a matched
/// OSC C / OSC D cycle, or a shell-specific equivalent fires).
///
/// Crossing-the-boundary contract:
/// The tracker's job ends at "raw capture plus boundary offsets"; it
/// does NOT assemble a cleaned string, truncate, spill to disk, or
/// populate a cache entry. The worker consumes this snapshot on the
/// shared finalize-once path and runs settle / echo-strip / clean /
/// truncate / cache in one place so the inline
/// <c>execute_command</c> path and the deferred
/// <c>wait_for_completion</c> path can never diverge.
///
/// Ownership:
/// <see cref="Capture"/> is borrowed, not owned. The worker is
/// responsible for calling <see cref="CommandOutputCapture.Complete"/>
/// once it has read every slice it needs. Keeping the reference
/// here (rather than dumping the slice into a string up front) is
/// what lets the finalizer read the command window lazily, possibly
/// stream-style via <see cref="CommandOutputCapture.OpenSliceReader"/>
/// for oversized captures, without ever materializing the full
/// capture as a single string.
///
/// Echo-stripping input:
/// <see cref="PtyPayloadBaseline"/> is the exact UTF-16 payload the
/// worker wrote to the PTY at command registration time (including
/// the multi-line tempfile wrapper form, where the payload is
/// <c>. 'tmp.ps1'; Remove-Item ...</c> rather than the user's
/// original command). Persisting it in the snapshot means the
/// finalize-once path can run <c>deterministic_byte_match</c>
/// echo stripping without re-deriving the payload from the adapter
/// + command — a derivation that would drift any time the worker
/// grows a new multi-line wrapper variant.
/// </summary>
/// <param name="Capture">
/// Raw output store (borrowed). The finalizer reads the command
/// window via <see cref="CommandOutputCapture.ReadSlice"/> /
/// <see cref="CommandOutputCapture.OpenSliceReader"/> and disposes
/// the capture when it is done.
/// </param>
/// <param name="CommandStart">
/// Absolute char offset (into <paramref name="Capture"/>) at which
/// the command's real output window begins. Today this is the
/// position of OSC C, or <c>0</c> when the adapter declares an
/// <c>input_echo_strategy</c> of <c>deterministic_byte_match</c>
/// and therefore never emits OSC C.
/// </param>
/// <param name="CommandEnd">
/// Absolute char offset at which the command's output window ends
/// (the position of OSC D). Always greater than or equal to
/// <paramref name="CommandStart"/>; equal when the command produced
/// no output.
/// </param>
/// <param name="ExitCode">
/// Exit code reported via OSC D. Worker decides how to render it
/// per shell family (e.g. cmd.exe cannot expose real
/// <c>%ERRORLEVEL%</c>, so cmd consoles format a neutral status
/// line).
/// </param>
/// <param name="Duration">
/// Wall-clock duration of the command, already formatted to a single
/// decimal place (matches the existing wire contract).
/// </param>
/// <param name="Cwd">
/// Working directory reported via OSC P (if any). Null when the shell
/// never emitted a <c>Cwd=</c> key between OSC D and OSC A.
/// </param>
/// <param name="Command">
/// Exact command text the AI asked to run, unmodified. Used for
/// status-line formatting; echo-stripping uses
/// <paramref name="PtyPayloadBaseline"/> instead.
/// </param>
/// <param name="ShellFamily">
/// Normalized shell family (e.g. "pwsh", "bash", "cmd"). Drives
/// shell-specific settle / clean rules in the finalizer.
/// </param>
/// <param name="DisplayName">
/// Proxy-supplied display identity, captured at registration time so
/// the worker can bake a self-contained status line into the cache
/// entry without depending on later proxy-side metadata (§7).
/// </param>
/// <param name="PostPromptSettleMs">
/// How long to wait after OSC A for trailing bytes that still belong
/// to this command result (adapter.output.post_prompt_settle_ms).
/// Worker passes zero for shell families that emit OSC A as part of
/// the prompt function (pwsh/powershell) — those never stream data
/// after OSC A.
/// </param>
/// <param name="InputEchoStrategy">
/// Adapter's echo strategy. The finalizer branches on this to decide
/// whether to run <see cref="PtyPayloadBaseline"/>-based stripping.
/// </param>
/// <param name="InputEchoLineEnding">
/// Line ending the worker appended when writing
/// <paramref name="PtyPayloadBaseline"/> to the PTY. Dropped from
/// the echo-strip target because it is the Enter keystroke and
/// never appears in the echoed text.
/// </param>
/// <param name="PtyPayloadBaseline">
/// Exact PTY payload written to trigger this command. For multi-line
/// pwsh / bash / cmd commands this is the <c>. 'tmp.ps1'</c> /
/// <c>call "tmp.cmd"</c> / <c>. 'tmp.sh'</c> wrapper — NOT the AI's
/// original command body. The finalizer uses it verbatim for
/// <c>deterministic_byte_match</c> echo stripping.
/// </param>
/// <param name="PromptStartOffset">
/// Absolute char offset at which OSC A (PromptStart) fired, measured
/// from the start of <paramref name="Capture"/>. The finalizer caps
/// its read slice here so prompt bytes emitted after OSC A never
/// bleed into the command's cleaned output — non-pwsh shells
/// (bash, cmd) stream real prompt text (<c>$ </c>, <c>bash-5.1$ </c>,
/// etc.) immediately after OSC A, and without this cap the extended
/// <c>effectiveEnd</c> rule that picks up delayed trailing command
/// output (Format-Table rows, cmd PROMPT repaint) would also swallow
/// the subsequent prompt characters. Null when the snapshot fires
/// without the tracker having seen OSC A — shouldn't happen under
/// normal flow (OSC A is what triggers snapshot emission), but kept
/// nullable so adapters / early-exit paths that bypass OSC A can
/// still produce a snapshot with a safe "no cap" fallback.
/// </param>
/// <param name="Generation">
/// Monotonic per-command token the tracker assigns at
/// <c>RegisterCommand</c> time. The finalize-once path echoes it back
/// via <c>CommandTracker.ReleaseAiCommand</c> so the tracker only
/// clears its <c>_isAiCommand</c> busy flag for the command that
/// actually produced this snapshot. A newer command that arrived
/// while the previous finalize was still draining bumps the
/// generation; the old finalize's release is then a no-op and the
/// new command's busy state survives intact. Without this token the
/// worker's <c>Status</c> would briefly flip to <c>"standby"</c>
/// between snapshot emission and cache insertion, matching the
/// ConsoleManager "cache lost" pattern and dropping the timed-out
/// command's PID before its result ever lands in
/// <c>_cachedResults</c>.
/// </param>
/// <param name="InlineDeliveryId">
/// Per-registration routing id the worker allocates at
/// <c>RegisterCommand</c> time and threads through the snapshot so
/// the finalize-once path can look the matching inline TCS up in
/// <c>ConsoleWorker._inlineDeliveriesById</c> instead of reading a
/// shared single-slot field. Without this id, two concurrent
/// <c>execute</c> requests that sneak past the tracker's
/// <c>Busy</c> gate could overwrite each other's delivery slot and
/// cross-deliver results (A's snapshot completing B's awaiter).
/// Null when the snapshot is produced outside the worker's
/// execute path (e.g. directly from a test that does not wire
/// an inline delivery) — the finalize-once path falls through to
/// the cache branch in that case.
/// </param>
public sealed record CompletedCommandSnapshot(
    CommandOutputCapture Capture,
    long CommandStart,
    long CommandEnd,
    int ExitCode,
    string Duration,
    string? Cwd,
    string Command,
    string? ShellFamily,
    string? DisplayName,
    int PostPromptSettleMs,
    string? InputEchoStrategy,
    string InputEchoLineEnding,
    string PtyPayloadBaseline,
    long? PromptStartOffset,
    long Generation,
    long? InlineDeliveryId = null);
