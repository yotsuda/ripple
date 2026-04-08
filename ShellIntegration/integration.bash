# shellpilot shell integration for bash
# Injects OSC 633 escape sequences for command lifecycle tracking.
# Sourced automatically by shellpilot console worker.

# Guard against double-sourcing
if [[ "$__SHELLPILOT_INJECTED" == "1" ]]; then
    return
fi
__SHELLPILOT_INJECTED=1

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

# Preexec: runs after prompt, before command execution (via DEBUG trap)
__sp_preexec() {
    if [[ "$BASH_COMMAND" == "__sp_precmd" ]] || \
       [[ "$BASH_COMMAND" == "${PROMPT_COMMAND}"* ]]; then
        return
    fi

    __sp_osc "C"
    __sp_in_command=1
}

# Install hooks
PROMPT_COMMAND="__sp_precmd"
trap '__sp_preexec' DEBUG

# Initial marker
__sp_osc "B"
