# shellpilot shell integration for PowerShell (pwsh.exe / powershell.exe)
# Injects OSC 633 escape sequences for command lifecycle tracking.
# Uses [char] escapes for compatibility with Windows PowerShell 5.1.
#
# OSC sequences are embedded in the prompt return string so they flow
# through the ConPTY console screen buffer (not Console.Out which is
# a separate stream in ConPTY environments).

if ($global:__ShellPilotInjected) { return }
$global:__ShellPilotInjected = $true

$global:__sp_ESC = [char]0x1B
$global:__sp_BEL = [char]7

function global:__sp_osc_str([string]$code) {
    return "$($global:__sp_ESC)]633;$code$($global:__sp_BEL)"
}

# Override prompt function
$global:__sp_original_prompt = $function:prompt
$global:__sp_last_history_id = 0

function global:prompt {
    $prefix = ""

    # Detect if a command was executed since last prompt
    $lastCmd = Get-History -Count 1 -ErrorAction SilentlyContinue
    if ($lastCmd -and $lastCmd.Id -ne $global:__sp_last_history_id) {
        $global:__sp_last_history_id = $lastCmd.Id

        # CommandExecuted (C) + CommandFinished (D) with exit code
        $prefix += (__sp_osc_str "C")
        $ec = if ($null -ne $global:LASTEXITCODE) { $global:LASTEXITCODE } else { 0 }
        $prefix += (__sp_osc_str "D;$ec")
    }

    # Report cwd
    $prefix += (__sp_osc_str "P;Cwd=$($PWD.Path)")

    # PromptStart (A) — triggers command completion detection
    $prefix += (__sp_osc_str "A")

    # Call original prompt
    $originalOutput = if ($global:__sp_original_prompt) {
        & $global:__sp_original_prompt
    } else {
        "PS $($PWD.Path)> "
    }

    # Return: OSC prefix + original prompt text
    return $prefix + $originalOutput
}

# Emit initial CommandInputStart marker via Write-Host (goes to console screen buffer)
Write-Host -NoNewline (__sp_osc_str "B")
