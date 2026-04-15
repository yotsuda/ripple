# splash

<div align="center">
  <img src="https://github.com/user-attachments/assets/1343f694-1c05-4899-9faa-d2b1138aa3ba" alt="social-image" width="640" />
</div>

**A shell MCP server for AI that actually holds a session.** Load `Import-Module Az` once and let AI run 50 follow-up cmdlets in milliseconds each. Watch every command happen in a real terminal window — the same one you can type into yourself.

> **Renamed from `splashshell`.** Previously published on npm as [`splashshell`](https://www.npmjs.com/package/splashshell) (v0.1.0 – v0.5.0). Starting with v0.7.0 the package lives at `@ytsuda/splash`. `splashshell` is deprecated; please migrate by uninstalling it and installing `@ytsuda/splash`.

## Install

No runtime prerequisite — splash ships as a self-contained NativeAOT binary (~13 MB, Windows x64). `npx` fetches it on first run.

```bash
claude mcp add-json splash -s user '{"command":"npx","args":["-y","@ytsuda/splash@latest"]}'
```

<details>
<summary>Claude Desktop</summary>

Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "splash": {
      "command": "npx",
      "args": ["-y", "@ytsuda/splash@latest"]
    }
  }
}
```

The `@latest` tag is important: without it, npx will happily keep reusing a stale cached copy even after a new version ships.

</details>

## Why splash?

Other shell MCP servers are either **stateless** (fresh subshell per command, nothing persists) or **headless** (persistent PTY, but invisible to you). splash is neither — and that unlocks things the others can't do.

### PowerShell becomes a first-class AI environment

Session persistence helps every shell, but for **PowerShell it's transformative**. Most MCP shell servers spin up a fresh subshell per command — which makes real PowerShell workflows impractical:

- **10,000+ modules on [PowerShell Gallery](https://www.powershellgallery.com/).** Az (Azure), AWS.Tools, Microsoft.Graph (Entra ID / M365), ExchangeOnlineManagement, PnP.PowerShell, SqlServer, ActiveDirectory — plus every CLI in PATH (git, docker, kubectl, terraform, gh, az, aws, gcloud) and full access to .NET types.
- **30–70 second cold imports, paid once.** `Import-Module Az.Compute, Az.Storage, Az.Network` can take over a minute on the first call. A subshell-per-command MCP server pays that cost on *every* command and the AI gives up on Azure workflows entirely. With splash, the AI imports once and every subsequent cmdlet runs in milliseconds.
- **Live .NET object graphs.** PowerShell pipes rich objects, not text. After `$vms = Get-AzVM -Status`, the AI can chain arbitrary follow-ups against the live object — filter, group, drill into nested properties — without re-hitting Azure. In a one-shot MCP server, that object vanishes the moment the command returns.
- **Interactive build-up of complex work.** Set a variable, inspect it, reshape it, feed it back into the next cmdlet. Build a multi-step workflow one command at a time with every previous step's result still in scope.

```powershell
# Command 1 — cold import, paid once for the whole session
Import-Module Az.Compute, Az.Storage

# Command 2 — instant; capture into a variable
$vms = Get-AzVM -Status

# Command 3 — instant; same session, $vms still in scope
$vms | Where-Object PowerState -eq "VM running" |
    Group-Object Location | Sort-Object Count -Descending
```

PowerShell on splash is the difference between **"AI can answer one-off questions"** and **"AI can do real infrastructure work."** bash and cmd are fully supported too, but pwsh is where splash shines.

### Full transparency, in both directions

splash opens a **real, visible terminal window**. You see every AI command as it runs — same characters, same output, same prompt — and you can type into the same window yourself at any time. When a command hangs on an interactive prompt, stalls in watch mode, or just needs a Ctrl+C, the AI can read what's currently on the screen and send keystrokes (Enter, y/n, arrow keys, Ctrl+C) back to the running command — diagnosing and responding without human intervention.

### Language REPLs, not just shells

splash isn't limited to the four shells (pwsh/powershell, bash, zsh, cmd) — it also hosts **eight language REPLs**: **python**, **node**, **racket**, **ccl** / **abcl** (Common Lisp), **fsi** (F# Interactive), **jshell** (Java), and **groovy**. Same AI affordances apply: load a heavy setup once, pipe results through follow-ups, keep state, step through a multi-line investigation. Tell the AI to drive a **Groovy** REPL for a Spring Boot codebase exploration, a **Jupyter-less Python** session for data shaping, a **Clozure Common Lisp** break loop to debug a live image — all with the same `execute_command` and the same shared-terminal transparency as the shells.

## Tools

| Tool | Description |
|------|-------------|
| `start_console` | Open a visible terminal. Pick a shell (bash, pwsh, powershell, cmd). Reuses an existing standby of the same shell unless `reason` is provided. |
| `execute_command` | Run a pipeline. Optionally target a specific `shell`. Times out cleanly with output cached for `wait_for_completion`; timeout responses include a `partialOutput` snapshot for immediate diagnosis. |
| `wait_for_completion` | Block until busy consoles finish and retrieve cached output. |
| `peek_console` | Read-only snapshot of what a console is displaying. Windows reads the screen buffer directly; Linux/macOS uses a VT interpreter. Reports busy/idle state, running command, and elapsed time. |
| `send_input` | Send raw keystrokes to a **busy** console's PTY input. `\r` for Enter, `\x03` for Ctrl+C, `\x1b[A` for arrow up, etc. Max 256 chars. |

Plus Claude Code–compatible file primitives: `read_file`, `write_file`, `edit_file`, `search_files`, `find_files`.

Status lines include console name, shell family, exit code, duration, and cwd:

```
✓ #12345 Sapphire (bash) | Status: Completed | Pipeline: ls /tmp | Duration: 0.6s | Location: /tmp
```

## More features

- **Auto-routing when busy** — each console tracks its own cwd; when the active one is busy, the AI is routed to a sibling at the same cwd automatically.
- **Console re-claim** — consoles outlive their parent MCP process, so AI client restarts don't kill your modules or variables.
- **Multi-line PowerShell** — heredocs, foreach, try/catch, nested scriptblocks all work via tempfile dot-sourcing.
- **Sub-agent isolation** — parallel AI agents each get their own consoles so they don't clobber each other's shells.
- **Cwd drift detection** — manual `cd` in the terminal is detected and the AI is warned before it runs the wrong command in the wrong place.

> **Architecture diagram, full routing matrix, and source**: see [yotsuda/splash](https://github.com/yotsuda/splash#readme).

## Platform support

**Windows** is the primary target (ConPTY + Named Pipe, fully tested). Unix PTY fallback for Linux/macOS is experimental.

## Known limitations

- **cmd.exe exit codes always read as 0** — cmd's `PROMPT` can't expand `%ERRORLEVEL%` at display time, so AI commands show as `Finished (exit code unavailable)`. Use `pwsh` or `bash` for exit-code-aware work.
- **Don't `Remove-Module PSReadLine -Force` inside a pwsh session** — PSReadLine's background reader threads survive module unload and steal console input, hanging the next AI command. Not recoverable.

## License

MIT. Full release notes and source at [yotsuda/splash](https://github.com/yotsuda/splash).
