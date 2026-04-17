# ripple shell integration for zsh
# Injects OSC 633 escape sequences for command lifecycle tracking.
# Sourced automatically by ripple console worker.

if [[ "$__RIPPLE_INJECTED" == "1" ]]; then
    return
fi
__RIPPLE_INJECTED=1

__rp_osc() {
    printf '\e]633;%s\a' "$1"
}

# precmd hook: runs before each prompt
__rp_precmd() {
    local exit_code=$?
    __rp_osc "D;$exit_code"
    __rp_osc "P;Cwd=$(pwd)"
    __rp_osc "A"
}

# preexec hook: runs before command execution
__rp_preexec() {
    __rp_osc "C"
}

autoload -Uz add-zsh-hook
add-zsh-hook precmd __rp_precmd
add-zsh-hook preexec __rp_preexec

# Initial marker
__rp_osc "B"
