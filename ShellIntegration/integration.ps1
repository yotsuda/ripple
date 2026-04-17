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

function global:prompt {
    $prefix = ""

    # Detect if a command was executed since last prompt
    $lastCmd = Get-History -Count 1 -ErrorAction SilentlyContinue
    if ($lastCmd -and $lastCmd.Id -ne $global:__rp_last_history_id) {
        $global:__rp_last_history_id = $lastCmd.Id

        # CommandFinished with exit code. OSC C is emitted from
        # PreCommandLookupAction before the command runs, so by the time
        # we're in the prompt function it has already fired.
        $ec = if ($null -ne $global:LASTEXITCODE) { $global:LASTEXITCODE } else { 0 }
        $prefix += (__rp_osc_str "D;$ec")
    }

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
