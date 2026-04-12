# splashshell

<div align="center">
  <img src="https://github.com/user-attachments/assets/1343f694-1c05-4899-9faa-d2b1138aa3ba" alt="social-image" width="640" />
</div>

A universal MCP server that exposes any shell (bash, pwsh, powershell, cmd) as a Model Context Protocol server, so AI assistants can run real commands in a real terminal — visible to you, with session state that persists across calls.

- **Real terminal, real output.** Commands run in a visible ConPTY-backed console. You see every character the AI types, just as if you typed it yourself.
- **Multiple shells side by side.** bash, pwsh, cmd, and others can all be active at the same time. Switch between them per command.
- **Session state persists.** `cd`, environment variables, and shell history carry across calls — it's one continuous shell, not isolated subprocess spawns.
- **Shell integration built in.** OSC 633 markers delimit command boundaries cleanly, so output parsing is reliable even for interleaved prompts and long-running commands.
- **Console re-claim.** Consoles outlive their parent MCP process. When the AI client restarts, the next session reattaches to existing consoles.
- **Auto cwd handoff.** When a same-shell console is busy, a new one is auto-started in the source console's directory and your command runs immediately — no manual `cd` needed.
- **Peek at busy consoles.** `peek_console` reads the terminal's current display — on Windows via the native screen buffer API, on other platforms via a built-in VT interpreter. Diagnose stuck commands, watch mode, and interactive prompts without interrupting.
- **Send input to running commands.** `send_input` feeds keystrokes (Enter, Ctrl+C, arrow keys, text) to a busy console's PTY. Respond to `Read-Host` prompts, dismiss pauses, or interrupt runaway processes.
- **Multi-line command support.** Multi-line PowerShell (heredocs, foreach, try/catch, nested scriptblocks) is handled via tempfile dot-sourcing — session state persists, history stays clean.
- **Sub-agent isolation.** Allocate per-agent consoles with `is_subagent` + `agent_id` so parallel agents don't clobber each other's shells.

## Architecture

```mermaid
graph TB
    Client["MCP Client<br/>(Claude Code, etc.)"]

    subgraph Proxy["splashshell proxy (stdio MCP server)"]
        CM["Console Manager<br/>(cwd tracking, re-claim,<br/>cache drain, switching)"]
        Tools["start_console<br/>execute_command<br/>wait_for_completion<br/>peek_console / send_input<br/>read_file / write_file / edit_file<br/>search_files / find_files"]
    end

    subgraph Consoles["Visible Console Windows (each runs splash --console)"]
        subgraph C1["#9876 Sapphire (bash)"]
            PTY1["ConPTY + bash<br/>(+ OSC 633)"]
        end
        subgraph C2["#5432 Cobalt (pwsh)"]
            PTY2["ConPTY + pwsh<br/>(+ OSC 633)"]
        end
        subgraph C3["#1234 Topaz (cmd)"]
            PTY3["ConPTY + cmd.exe<br/>(+ OSC 633 via PROMPT)"]
        end
    end

    User["User"] -- "keyboard" --> C1
    User -- "keyboard" --> C2
    User -- "keyboard" --> C3
    Client -- "stdio" --> Proxy
    CM -- "Named Pipe" --> C1
    CM -- "Named Pipe" --> C2
    CM -- "Named Pipe" --> C3
    CM -. "auto-switch<br/>if busy" .-> C1
    CM -. "shell routing" .-> C2
```

## Install

No global install is required — `npx` fetches and runs splashshell on demand. The only prerequisite is the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (the package bundles a ~5.6 MB native `splash.exe` that needs it).

> The `@latest` tag is important: without it, npx will happily keep reusing a stale cached copy even after a new version ships.

### Claude Code

```bash
claude mcp add-json splash -s user '{"command":"npx","args":["-y","splashshell@latest"]}'
```

### Claude Desktop

Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "splash": {
      "command": "npx",
      "args": ["-y", "splashshell@latest"]
    }
  }
}
```

### Build from source (for development)

```bash
git clone https://github.com/yotsuda/splashshell.git
cd splashshell
dotnet publish -c Release -r win-x64 --no-self-contained -o ./dist
```

The binary is `./dist/splash.exe`. Use the absolute path instead of the `npx` command in your MCP config.

## Tools

### Shell tools

| Tool | Description |
|------|-------------|
| `start_console` | Open a visible terminal window. Pick a shell (bash, pwsh, powershell, cmd, or a full path). Optional `cwd`, `banner`, and `reason` parameters. Reuses an existing standby of the same shell unless `reason` is provided. |
| `execute_command` | Run a pipeline. Optionally specify `shell` to target a specific shell type — finds an existing console of that shell, or auto-starts one. Times out cleanly with output cached for `wait_for_completion`. On timeout, includes a `partialOutput` snapshot so the AI can diagnose stuck commands immediately. |
| `wait_for_completion` | Block until busy consoles finish and retrieve cached output (use after a command times out). |
| `peek_console` | Read-only snapshot of what a console is currently displaying. On Windows, reads the console screen buffer directly (exact match with the visible terminal). On Linux/macOS, uses a built-in VT terminal interpreter as fallback. Specify a console by display name or PID, or omit to peek at the active console. Reports busy/idle state, running command, and elapsed time. |
| `send_input` | Send raw keystrokes to a **busy** console's PTY input. Use `\r` for Enter, `\x03` for Ctrl+C, `\x1b[A` for arrow up, etc. Rejected when the console is idle (use `execute_command` instead). Console must be specified explicitly — no implicit routing, for safety. Max 256 chars per call. |

Status lines include the console name, shell family, exit code, duration, and current directory:

```
✓ #12345 Sapphire (bash) | Status: Completed | Pipeline: ls /tmp | Duration: 0.6s | Location: /tmp
```

**Busy console workflow:** When `execute_command` times out, the AI can `peek_console` to see what's happening (watch mode? interactive prompt? stalled progress?), then either `send_input` to respond (Enter, Ctrl+C, y/n) or `wait_for_completion` to wait for the natural finish.

Each MCP tool call also drains:

- **Cached results** from any console whose timed-out command has since finished
- **Closed console notifications** when a console window has been closed since the last call

### File tools

Claude Code–compatible file primitives, useful when the MCP client doesn't already provide them.

| Tool | Description |
|------|-------------|
| `read_file` | Read a file with line numbers. Supports `offset` / `limit` for paging through large files. Detects binary files. |
| `write_file` | Create or overwrite a file. Creates parent directories as needed. |
| `edit_file` | Replace an exact string in a file. Old string must be unique by default; pass `replace_all` to replace every occurrence. |
| `search_files` | Search file contents with a regular expression. Returns matching lines with file paths and line numbers. Supports `glob` filtering. |
| `find_files` | Find files by glob pattern (e.g., `**/*.cs`). Returns matching paths. |

## Multi-shell behavior

splashshell tracks the cwd of every console and can switch transparently between same-shell consoles:

| Scenario | Behavior |
|---|---|
| First execute on a new shell | Auto-starts a console; warns so you can verify cwd before re-executing |
| Active console matches requested shell | Runs immediately |
| Active console busy, same shell requested | Auto-starts a sibling console **at the source console's cwd** and runs immediately |
| Switch to a same-shell standby | Prepends `cd` preamble so the command runs in the source cwd, then executes |
| Switch to a different shell | Warns to confirm cwd (cross-shell path translation is not implemented) |
| User manually `cd`'d in the active console | Warns so the AI can verify the new cwd before running its next command |

Window titles use the format `#PID Name` (e.g., `#12345 Sapphire`) so you can identify each console at a glance. When the parent MCP process exits, titles change to `#PID ~~~~` to indicate the console is up for re-claim.

## Platform support

- **Windows**: ConPTY + Named Pipe (primary target, fully tested)
- **Linux/macOS**: Unix PTY fallback (experimental)

## How it works

splashshell runs as a stdio MCP server. When the AI calls `start_console`, splashshell spawns itself in `--console` mode as a ConPTY worker, which hosts the actual shell (cmd.exe, pwsh.exe, bash.exe, etc.) inside a real Windows console window. The parent process streams stdin/stdout over a named pipe, injects shell integration scripts (`ShellIntegration/integration.*`) to emit OSC 633 markers, and parses those markers to delimit command output, track cwd, and capture exit codes.

Result: the AI gets structured command-by-command output, the user gets a real terminal they can type into, and session state (cwd, env, history) persists across every call.

## License

MIT
