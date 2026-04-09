# shellpilot

A universal MCP server that exposes any shell (bash, pwsh, zsh) as a Model Context Protocol server, so AI assistants can run real commands in a real terminal — visible to you, with session state that persists across calls.

- **Real terminal, real output.** Commands run in a visible ConPTY-backed console. You see every character the AI types, just as if you typed it yourself.
- **Session state persists.** `cd`, environment variables, and shell history carry across calls — it's one continuous shell, not isolated subprocess spawns.
- **Shell integration built in.** OSC 633 markers delimit command boundaries cleanly, so output parsing is reliable even for interleaved prompts and long-running commands.
- **Sub-agent isolation.** Allocate per-agent consoles with `is_subagent` + `agent_id` so parallel agents don't clobber each other's shells.

## Install

Requires [.NET 9 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
git clone https://github.com/yotsuda/shellpilot.git
cd shellpilot
dotnet publish -c Release -r win-x64 --no-self-contained -o ./dist
```

The binary is `./dist/shellpilot.exe`.

## MCP Setup

### Claude Code

```bash
claude mcp add shell -s user -- C:\path\to\shellpilot.exe
```

### Claude Desktop

Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "shell": {
      "command": "C:\\path\\to\\shellpilot.exe"
    }
  }
}
```

## Tools

### Shell tools

| Tool | Description |
|------|-------------|
| `start_console` | Open a visible terminal window. Shell is locked per session (bash, pwsh, zsh, or a full path). |
| `execute_command` | Run a command in the shared terminal. Output streams back with exit code, duration, and cwd. |
| `wait_for_completion` | Wait for busy consoles to finish and retrieve cached output (use after a command times out). |

### File tools

`read_file`, `write_file`, `edit_file`, `find_files`, `search_files` — Claude Code–compatible file primitives, useful when the MCP client doesn't already provide them.

## Platform support

- **Windows**: ConPTY + Named Pipe (primary target, fully tested)
- **Linux/macOS**: Unix PTY fallback (experimental)

## How it works

shellpilot runs as a stdio MCP server. When the AI calls `start_console`, shellpilot spawns itself in `--console` mode as a ConPTY worker, which hosts the actual shell (cmd.exe, pwsh.exe, bash.exe, etc.) inside a real Windows console window. The parent process streams stdin/stdout over a named pipe, injects shell integration scripts (`ShellIntegration/integration.*`) to emit OSC 633 markers, and parses those markers to delimit command output.

Result: the AI gets structured command-by-command output, the user gets a real terminal they can type into, and session state (cwd, env, history) persists across every call.

## License

MIT
