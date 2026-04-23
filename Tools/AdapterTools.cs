using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Ripple.Services;
using Ripple.Services.Adapters;

namespace Ripple.Tools;

/// <summary>
/// MCP tools that expose ripple's adapter registry to AI consumers.
/// Moves the "what shells / REPLs does this ripple build know about?"
/// query off the startup-stderr banner and onto an explicit, structured
/// tool call — AIs that need the list get the list; runs that do not
/// stay quiet.
///
/// AOT-safe: writes JSON via <see cref="Utf8JsonWriter"/>, no reflection
/// over anonymous types. Mirrors the convention already established in
/// <see cref="Services.PipeJson"/>.
/// </summary>
[McpServerToolType]
public class AdapterTools
{
    [McpServerTool]
    [Description("List every registered value this ripple build accepts as the `shell` argument to execute_command / start_console — shells (bash, pwsh, cmd, zsh), REPLs (python, node, duckdb, fsi, jshell, groovy, lua, racket, sbcl, ccl, abcl, deno, sqlite3, psql), and debuggers (pdb, perldb, jdb). Use this when a caller asks which shells/REPLs are available, when deciding which `shell` value to pass, or when troubleshooting why an expected shell is not usable. Any absolute path can also be passed as `shell` (e.g. `C:\\tools\\myrepl.exe`) — ripple will launch whatever lives at that path even when it is not in this list, but without a matching adapter it runs in a minimal mode: no prompt-boundary detection, no exit-code reporting, and no cwd tracking. Drop a YAML in ~/.ripple/adapters/ to make a custom REPL first-class (it then shows up here with full metadata). Returns a JSON object with a `shells` array and a `load_issues` object. Each shell entry carries name, aliases, description, family (shell | repl | debugger), source (embedded — shipped in the binary — or external — loaded from ~/.ripple/adapters), executable (the resolved absolute path start_console / execute_command would actually launch — null when the name / override is not on PATH), and executable_note (a short explanation when executable is null, e.g. 'not found in PATH; tried: pwsh'). `load_issues` surfaces parse_errors, collisions, and overrides from startup; empty arrays mean a clean load.")]
    public static string ListShells()
    {
        var registry = AdapterRegistry.Default;
        var report = AdapterRegistry.DefaultReport;

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();

            w.WriteStartArray("shells");
            if (registry is not null)
            {
                foreach (var a in registry.All.OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("name", a.Name);
                    w.WriteStartArray("aliases");
                    if (a.Aliases is { } aliases)
                        foreach (var alias in aliases) w.WriteStringValue(alias);
                    w.WriteEndArray();
                    w.WriteString("description", a.Description);
                    w.WriteString("family", a.Family);
                    w.WriteString("source", a.Source);

                    var launch = a.ResolveLaunchExecutable();
                    if (launch.ResolvedPath is not null)
                    {
                        w.WriteString("executable", launch.ResolvedPath);
                    }
                    else
                    {
                        w.WriteNull("executable");
                        w.WriteString("executable_note",
                            $"not found in PATH; tried: {string.Join(", ", launch.Attempted)}");
                    }
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();

            w.WriteStartObject("load_issues");

            w.WriteStartArray("parse_errors");
            if (report is not null)
            {
                foreach (var e in report.ParseErrors)
                {
                    w.WriteStartObject();
                    w.WriteString("source", e.Source.ToString().ToLowerInvariant());
                    w.WriteString("resource", e.Resource);
                    w.WriteString("error", e.Error);
                    w.WriteBoolean("user_actionable",
                        e.Source == AdapterLoader.AdapterSource.External);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();

            w.WriteStartArray("collisions");
            if (report is not null)
            {
                foreach (var c in report.Collisions)
                {
                    w.WriteStartObject();
                    w.WriteString("message", c.Message);
                    w.WriteBoolean("user_actionable", c.IsUserActionable);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();

            w.WriteStartArray("overrides");
            if (report is not null)
                foreach (var o in report.Overrides) w.WriteStringValue(o);
            w.WriteEndArray();

            w.WriteEndObject();

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
