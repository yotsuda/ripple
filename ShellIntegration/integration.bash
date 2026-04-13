# splashshell shell integration for bash
# Injects OSC 633 escape sequences for command lifecycle tracking.
# Sourced automatically by splashshell console worker.

# Guard against double-sourcing
if [[ "$__SPLASHSHELL_INJECTED" == "1" ]]; then
    return
fi
__SPLASHSHELL_INJECTED=1

# Save original PROMPT_COMMAND
__sp_original_prompt_command="$PROMPT_COMMAND"

# Track state
__sp_in_command=0

# Emit OSC 633 sequence: \e]633;{code}[;{data}]\a
__sp_osc() {
    printf '\e]633;%s\a' "$1"
}

# Precmd: runs before each prompt
__sp_precmd() {
    local exit_code=$?

    if [[ "$__sp_in_command" == "1" ]]; then
        __sp_osc "D;$exit_code"
        __sp_in_command=0
    fi

    __sp_osc "P;Cwd=$(pwd)"
    __sp_osc "A"

    if [[ -n "$__sp_original_prompt_command" ]]; then
        eval "$__sp_original_prompt_command"
    fi
}

# Preexec: runs after prompt, before command execution (via DEBUG trap).
# DEBUG fires for every BASH_COMMAND including sub-commands inside a
# pipeline / sequence / function / sourced script — but the OSC 633
# protocol expects exactly one C per "command line submit" so the proxy
# tracker can mark the command-start position once and capture the entire
# block. Gate on __sp_in_command so subsequent sub-command DEBUG calls in
# the same line are no-ops; __sp_precmd resets the flag at the next prompt.
__sp_preexec() {
    if [[ "$BASH_COMMAND" == "__sp_precmd" ]] || \
       [[ "$BASH_COMMAND" == "${PROMPT_COMMAND}"* ]]; then
        return
    fi

    if [[ "$__sp_in_command" != "1" ]]; then
        __sp_osc "C"
        __sp_in_command=1
    fi
}

# Install hooks
PROMPT_COMMAND="__sp_precmd"
trap '__sp_preexec' DEBUG

# Initial marker
__sp_osc "B"
