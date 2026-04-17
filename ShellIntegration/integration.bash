# ripple shell integration for bash
# Injects OSC 633 escape sequences for command lifecycle tracking.
# Sourced automatically by ripple console worker.

# Guard against double-sourcing
if [[ "$__RIPPLE_INJECTED" == "1" ]]; then
    return
fi
__RIPPLE_INJECTED=1

# Save original PROMPT_COMMAND
__rp_original_prompt_command="$PROMPT_COMMAND"

# Emit OSC 633 sequence: \e]633;{code}[;{data}]\a
__rp_osc() {
    printf '\e]633;%s\a' "$1"
}

__rp_emit_c_pending=0

# Precmd: runs before each prompt via PROMPT_COMMAND. Emits OSC 633 D
# (CommandFinished with exit code), P (cwd), A (PromptStart) so the
# proxy tracker can close out the command just resolved. OSC D fires
# unconditionally — even if no command ran this cycle (empty Enter,
# Ctrl+C at idle prompt), the tracker's _commandStart gate will keep
# stray emissions from spuriously resolving.
__rp_precmd() {
    local exit_code=$?

    __rp_osc "D;$exit_code"
    __rp_osc "P;Cwd=$(pwd)"
    __rp_osc "A"

    if [[ -n "$__rp_original_prompt_command" ]]; then
        eval "$__rp_original_prompt_command"
    fi

    # Arm OSC C for the next user command. The DEBUG trap below fires it
    # right before bash runs the first simple command of that submission,
    # which is exactly the boundary the proxy tracker treats as "real
    # command output starts here".
    __rp_emit_c_pending=1
}

# DEBUG trap: fires before every top-level simple command bash is about
# to execute. Emit OSC 633 C exactly once per armed prompt cycle — the
# BASH_COMMAND guard skips the bash-triggered call to __rp_precmd
# itself, which would otherwise inject a spurious OSC C between the
# previous command's output and the next prompt redraw.
#
# Why DEBUG trap instead of PS0:
#   PS0 is cleaner (single expansion-time hook, no recursion risk) but
#   was only added in bash 4.4. macOS ships /bin/bash at 3.2 (last GPLv2
#   release Apple is willing to bundle), where PS0 is silently ignored
#   — producing a ripple worker that emits OSC D/P/A but never OSC C,
#   which leaves the tracker's resolve-on-PromptStart gate permanently
#   unmet and every AI command hitting the full execute timeout. DEBUG
#   works back to bash 2.x so this path covers every bash ripple can
#   plausibly run against.
#
# `set -T` (functrace) is intentionally NOT enabled: we want DEBUG to
# fire for top-level user commands only. Without functrace, inner
# commands inside __rp_precmd / __rp_osc don't fire DEBUG, so we get
# the filter behaviour "for free" instead of needing to enumerate
# every helper name in the BASH_COMMAND guard.
__rp_debug() {
    (( __rp_emit_c_pending )) || return
    [[ "$BASH_COMMAND" == "__rp_precmd" ]] && return
    __rp_emit_c_pending=0
    __rp_osc "C"
}

PROMPT_COMMAND="__rp_precmd"
trap __rp_debug DEBUG

# Initial marker — emitted once at integration load so the proxy tracker
# can distinguish "shell still starting up" from "shell ready for the
# first user command". The initial OSC A from __rp_precmd's first prompt
# cycle flips the tracker's _shellReady flag.
__rp_osc "B"
