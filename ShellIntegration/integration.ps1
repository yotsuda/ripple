# ripple shell integration for PowerShell (pwsh.exe / powershell.exe)
# Injects OSC 633 escape sequences for command lifecycle tracking.
# Uses [char] escapes for compatibility with Windows PowerShell 5.1.
#
# Event sequence (for both AI-initiated and user-typed commands):
#   OSC B (Enter pressed) → PSReadLine AcceptLine finalize →
#   OSC C (command about to execute) → command output →
#   OSC D;{exitCode} (command finished) → OSC P;Cwd=... → OSC A (prompt ready) →
#   prompt text rendered → PSReadLine input loop (prediction, etc.)
#
# ripple captures just the region between OSC C and OSC D as the command
# output. Bytes outside that window (typing animation, AcceptLine finalize,
# prompt repaint, PSReadLine prediction) are ignored, so no heuristic
# "strip up to first newline" logic is needed.

if ($global:__RippleInjected) { return }
$global:__RippleInjected = $true

$global:__rp_ESC = [char]0x1B
$global:__rp_BEL = [char]7

function global:__rp_osc_str([string]$code) {
    return "$($global:__rp_ESC)]633;$code$($global:__rp_BEL)"
}

# Override prompt function — emits OSC D (command finished with exit code),
# OSC P (cwd), OSC A (prompt ready) so ripple can detect the end of the
# command's output and drain its capture window. OSC C is NOT emitted here;
# it now lives on PreCommandLookupAction below so the "execution started"
# marker fires BEFORE the command writes anything to the console.
$global:__rp_original_prompt = $function:prompt
$global:__rp_last_history_id = 0
# Snapshot of $LASTEXITCODE at the moment PreCommandLookupAction fires
# (i.e. just before the user / AI pipeline actually runs). The prompt fn
# compares the post-command value against this to decide whether
# $LASTEXITCODE reflects THIS pipeline's native exit or is residue from
# a prior native invocation. Without this, a pure-PowerShell pipeline
# (no native exe) would inherit whatever $LASTEXITCODE happened to be
# left from an earlier `cmd /c "exit 7"` and ripple would report the
# innocent pipeline as Failed (exit 7) — observed and reproduced.
$global:__rp_lec_at_cmd_start = $null
# Multi-line AI pipelines run inside a dot-sourced tempfile wrapped by
# `Import-Module PSReadLine; . 'tempfile'; Remove-Item '...'`. The prompt
# fn's naive $? would reflect Remove-Item (always true), not the user
# pipeline inside the tempfile. ConsoleWorker.BuildMultiLineTempfileBody
# now stashes the tempfile's own $? / $LASTEXITCODE into these globals
# as its final two statements; the prompt fn reads them with priority,
# then clears, so subsequent single-line commands fall through to plain
# $?-based detection.
$global:__rp_ai_pipeline_ok = $null
$global:__rp_ai_pipeline_lec = $null
# Snapshot of $Error.Count at PreCommandLookupAction. The prompt fn
# computes ($Error.Count - this) to report the number of error records
# the pipeline added — written to OSC 633;E;{N} so the proxy can
# surface it as `Errors: N` in the status line. Errors are PowerShell's
# canonical "something failed" signal: cmdlet non-terminating errors,
# `Write-Error`, thrown exceptions all populate $Error. Native exe
# non-zero exits do NOT — those are covered by OSC D's $? path. Warning
# / Information streams don't have an analogous reliable counter (no
# global `$Warning.Count`; cmdlets emit warnings via the engine pipe,
# bypassing any Write-Warning proxy), so only errors are counted.
$global:__rp_err_count_at_cmd_start = 0

function global:prompt {
    # CRITICAL: $? must be captured on the very first line — any statement
    # below (including simple assignments) can reset it. $? is the
    # canonical "did the last pipeline succeed" indicator in PowerShell:
    # true for successful cmdlets / successful natives (exit 0) /
    # successful statements, false for cmdlet errors / native non-zero
    # exit / thrown exceptions. Using $? as the prime signal — with
    # $LASTEXITCODE only consulted when $? is false AND the value was
    # updated by this pipeline — eliminates the "stale $LASTEXITCODE
    # from a prior native bleeds into every subsequent innocent
    # pipeline" bug.
    $ok = $?
    $lec = $global:LASTEXITCODE
    $lecAtStart = $global:__rp_lec_at_cmd_start
    $aiOk = $global:__rp_ai_pipeline_ok
    $aiLec = $global:__rp_ai_pipeline_lec

    # Consume the multi-line AI stash immediately so the next command
    # (which may be user-typed or single-line AI) sees clean slots.
    $global:__rp_ai_pipeline_ok = $null
    $global:__rp_ai_pipeline_lec = $null

    $prefix = ""

    # Detect if a command was executed since last prompt
    $lastCmd = Get-History -Count 1 -ErrorAction SilentlyContinue
    if ($lastCmd -and $lastCmd.Id -ne $global:__rp_last_history_id) {
        $global:__rp_last_history_id = $lastCmd.Id

        # CommandFinished with exit code. OSC C is emitted from
        # PreCommandLookupAction before the command runs, so by the time
        # we're in the prompt function it has already fired.
        #
        # Exit code resolution, in order:
        #   (1) Multi-line AI stash is set → use it verbatim. The
        #       tempfile's own last statement captured $? / $LASTEXITCODE
        #       before Remove-Item ran and reset them.
        #   (2) $ok is true → pipeline succeeded → 0.
        #   (3) $ok is false AND $LASTEXITCODE was updated by this
        #       pipeline AND the value is non-zero → use it (native exe
        #       really did fail with that code).
        #   (4) $ok is false otherwise → generic failure → 1. Using 1
        #       rather than leaking a stale $LASTEXITCODE keeps the AI's
        #       mental model honest: "the pipeline failed, exit code not
        #       meaningful as a native exit".
        if ($null -ne $aiOk) {
            $ec = if ($aiOk) { 0 }
                  elseif ($null -ne $aiLec -and $aiLec -ne 0) { $aiLec }
                  else { 1 }
        } else {
            $lecChanged = $lec -ne $lecAtStart
            $ec = if ($ok) { 0 }
                  elseif ($lecChanged -and $null -ne $lec -and $lec -ne 0) { $lec }
                  else { 1 }
        }
        $prefix += (__rp_osc_str "D;$ec")

        # Errors-this-pipeline count via $Error.Count delta. Floor at 0
        # so a user `$Error.Clear()` mid-command can't produce a negative
        # delta that breaks the int parser on the proxy side. The proxy
        # surfaces this as `Errors: N` in the status line when N > 0.
        $errDelta = $Error.Count - $global:__rp_err_count_at_cmd_start
        if ($errDelta -lt 0) { $errDelta = 0 }
        $prefix += (__rp_osc_str "E;$errDelta")
    }

    # Clear the pre-command snapshot so the next command starts fresh.
    # PreCommandLookupAction sets it again when the next pipeline begins.
    $global:__rp_lec_at_cmd_start = $null

    # Report cwd
    $prefix += (__rp_osc_str "P;Cwd=$($PWD.Path)")

    # PromptStart (A) — triggers command completion detection
    $prefix += (__rp_osc_str "A")

    # Call original prompt
    $originalOutput = if ($global:__rp_original_prompt) {
        & $global:__rp_original_prompt
    } else {
        "PS $($PWD.Path)> "
    }

    # Return: OSC prefix + original prompt text
    return $prefix + $originalOutput
}

# PreCommandLookupAction fires inside the PowerShell engine right before it
# resolves a command name to an invocation target — i.e. AFTER PSReadLine
# AcceptLine has finalized the input line and BEFORE the command produces
# any output. Emit OSC C here so ripple knows that whatever follows is real
# command output, not PSReadLine rendering. The action fires once per
# command lookup, including nested lookups inside a pipeline, so a flag set
# by the Enter key handler ensures we emit exactly once per user command.
$global:__rp_pending_user_command = $false

$ExecutionContext.InvokeCommand.PreCommandLookupAction = {
    param($commandName, $eventArgs)
    if ($global:__rp_pending_user_command) {
        $global:__rp_pending_user_command = $false
        # Snapshot $LASTEXITCODE so the prompt fn can distinguish "this
        # pipeline ran a native exe and that's where the value came from"
        # from "this pipeline was pure PowerShell and $LASTEXITCODE is
        # residue from an earlier native run". Without the snapshot, a
        # cmd.exe exit 7 followed by a pure-PS pipeline would be reported
        # as exit 7.
        $global:__rp_lec_at_cmd_start = $global:LASTEXITCODE
        # Same idea for $Error.Count: snapshot here so the prompt fn can
        # report the delta as the number of error records this pipeline
        # added. $Error is per-runspace and persists across commands; only
        # the delta is meaningful as "this pipeline's errors".
        $global:__rp_err_count_at_cmd_start = $Error.Count
        [Console]::Write((__rp_osc_str "C"))
    }
}

# Emit initial CommandInputStart marker via Write-Host (goes to console screen
# buffer). ripple uses this to mark the shell as "ready" (first PromptStart
# hasn't fired yet at integration-load time, but the subsequent prompt render
# will).
Write-Host -NoNewline (__rp_osc_str "B")

# PSReadLine integrations — best-effort. PSReadLine is normally loaded in
# interactive pwsh (ripple's launch line also calls Import-Module on it),
# but a screen-reader fallback or an explicit Remove-Module would leave the
# cmdlets undefined. Guard with Get-Module so the integration script stops
# cleanly instead of throwing CommandNotFoundException, which would crash
# the worker mid-startup. Without these handlers the AI command tracker
# stops getting OSC B (user-busy detection) and the tempfile dot-source
# lines leak into history — both observability concerns, not correctness.
if (Get-Module PSReadLine) {
    # Override Enter to emit OSC B and arm the "next command lookup is a
    # user command" flag. The actual OSC C fires from PreCommandLookupAction
    # a moment later, right before the command runs — by then PSReadLine's
    # AcceptLine has finalized the visible line and we're out of the input-
    # rendering noise.
    try {
        Set-PSReadLineKeyHandler -Key Enter -ScriptBlock {
            Write-Host -NoNewline (__rp_osc_str "B")
            $global:__rp_pending_user_command = $true
            [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
        }
    } catch { }

    # Skip the internal `. 'C:\...\.ripple-exec-*.ps1'; Remove-Item ...`
    # dot-source lines that ripple uses to run multi-line AI commands.
    # Those are an implementation detail — the user pressing Up to recall
    # history wants to see the real previous commands they typed, not the
    # transient tempfile path, and the scrollback already shows the
    # colorized echo of the actual command body via the tempfile's own
    # output.
    try {
        Set-PSReadLineOption -AddToHistoryHandler {
            param([string]$line)
            if ($line -match "\.ripple-exec-.*\.ps1") {
                return [Microsoft.PowerShell.AddToHistoryOption]::SkipAdding
            }
            return [Microsoft.PowerShell.AddToHistoryOption]::MemoryAndFile
        }
    } catch { }
}
