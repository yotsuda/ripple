# shellpilot shell integration for zsh
# Injects OSC 633 escape sequences for command lifecycle tracking.
# Sourced automatically by shellpilot console worker.

if [[ "$__SHELLPILOT_INJECTED" == "1" ]]; then
    return
fi
__SHELLPILOT_INJECTED=1

__sp_osc() {
    printf '\e]633;%s\a' "$1"
}

# precmd hook: runs before each prompt
__sp_precmd() {
    local exit_code=$?
    __sp_osc "D;$exit_code"
    __sp_osc "P;Cwd=$(pwd)"
    __sp_osc "A"
}

# preexec hook: runs before command execution
__sp_preexec() {
    __sp_osc "C"
}

autoload -Uz add-zsh-hook
add-zsh-hook precmd __sp_precmd
add-zsh-hook preexec __sp_preexec

# Initial marker
__sp_osc "B"
