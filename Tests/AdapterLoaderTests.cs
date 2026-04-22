using Ripple.Services.Adapters;

namespace Ripple.Tests;

/// <summary>
/// Contract tests for the adapter loader: verifies that every embedded
/// adapter YAML parses, that required fields are populated, and that the
/// registry exposes them under the names ConsoleManager.NormalizeShellFamily
/// will look up.
/// </summary>
public static class AdapterLoaderTests
{
    public static void Run(AdapterRegistry registry, AdapterRegistry.LoadReport report)
    {
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== AdapterLoader Tests ===");

        // Load errors are a hard fail — they mean a shipped adapter is broken.
        Assert(report.ParseErrors.Count == 0,
            $"no parse errors (got {report.ParseErrors.Count}: {string.Join("; ", report.ParseErrors.Select(e => $"{e.Resource}: {e.Error}"))})");

        Assert(report.Collisions.Count == 0,
            $"no name collisions (got {report.Collisions.Count}: {string.Join("; ", report.Collisions)})");

        // All four shell adapters we shipped should be present.
        Assert(registry.Count >= 4, $"at least 4 adapters registered (got {registry.Count})");

        // Per-adapter smoke checks. These are deliberately shallow — they
        // prove the YAML -> model mapping works end-to-end for each
        // section, without asserting specific values that may evolve.
        foreach (var name in new[] { "pwsh", "bash", "zsh", "cmd" })
        {
            var adapter = registry.Find(name);
            Assert(adapter != null, $"{name}: found in registry");
            if (adapter == null) continue;

            Assert(adapter.Schema == 1, $"{name}: schema v1");
            Assert(!string.IsNullOrEmpty(adapter.Name), $"{name}: name is set");
            Assert(!string.IsNullOrEmpty(adapter.Version), $"{name}: version is set");
            Assert(adapter.Family == "shell", $"{name}: family == shell");
            Assert(!string.IsNullOrEmpty(adapter.Process.CommandTemplate),
                $"{name}: process.command_template is set");
            Assert(!string.IsNullOrEmpty(adapter.Signals.Interrupt),
                $"{name}: signals.interrupt is set");
            Assert(!string.IsNullOrEmpty(adapter.Lifecycle.Shutdown.Command),
                $"{name}: lifecycle.shutdown.command is set");
        }

        // pwsh-specific: verify the integration_script block made it through
        // the YAML multi-line literal into the model verbatim.
        var pwsh = registry.Find("pwsh");
        if (pwsh != null)
        {
            Assert(!string.IsNullOrEmpty(pwsh.IntegrationScript),
                "pwsh: integration_script block is present");
            Assert(pwsh.IntegrationScript?.Contains("__RippleInjected") == true,
                "pwsh: integration_script contains __RippleInjected guard");
            Assert(pwsh.IntegrationScript?.Contains("PreCommandLookupAction") == true,
                "pwsh: integration_script contains PreCommandLookupAction hook");
            Assert(pwsh.Init.HookType == "precommand_lookup_action",
                "pwsh: init.hook_type == precommand_lookup_action");
            Assert(pwsh.Input.MultilineDelivery == "encoded_scriptblock",
                "pwsh: input.multiline_delivery == encoded_scriptblock");
            Assert(pwsh.Process.InheritEnvironment == false,
                "pwsh: process.inherit_environment == false (clean env)");
            Assert(pwsh.Capabilities.ExitCode == "true",
                "pwsh: capabilities.exit_code == true");
        }

        // bash-specific: PS0 hook, tempfile multiline delivery (corrected
        // in e5fec38 — the yaml used to claim 'direct' but HandleExecuteAsync
        // has always routed bash multiline through the hardcoded
        // isMultiLinePosix tempfile path), and the empirically-verified
        // line-editor clear_line opt-in that flushes buffered user
        // keystrokes before each AI command (see commit c90d3f1 for context).
        var bash = registry.Find("bash");
        if (bash != null)
        {
            Assert(bash.Init.HookType == "ps0", "bash: init.hook_type == ps0");
            Assert(bash.Input.MultilineDelivery == "tempfile",
                "bash: input.multiline_delivery == tempfile");
            Assert(bash.Process.InheritEnvironment == true,
                "bash: process.inherit_environment == true (MSYS2 needs it)");
            Assert(bash.Capabilities.JobControl == true,
                "bash: capabilities.job_control == true");
            Assert(bash.Input.ClearLine == "\u0001\u000b",
                "bash: input.clear_line == Ctrl-A+Ctrl-K (readline emacs mode)");
        }

        // zsh opts into the same clear_line sequence as bash since ZLE
        // defaults to emacs-mode bindings with Ctrl-A / Ctrl-K doing
        // beginning-of-line / kill-line.
        var zsh = registry.Find("zsh");
        if (zsh != null)
        {
            Assert(zsh.Input.ClearLine == "\u0001\u000b",
                "zsh: input.clear_line == Ctrl-A+Ctrl-K (ZLE emacs default)");
        }

        // cmd-specific: unreliable exit code + deterministic echo strip.
        // clear_line is deliberately null because cmd has no line editor
        // to target — Ctrl-A/Ctrl-K would be injected as literal input
        // characters into the command the user is building.
        var cmd = registry.Find("cmd");
        if (cmd != null)
        {
            Assert(cmd.Init.Strategy == "prompt_variable",
                "cmd: init.strategy == prompt_variable");
            Assert(cmd.Init.HookType == "none",
                "cmd: init.hook_type == none (no preexec available)");
            Assert(cmd.Capabilities.ExitCode == "unreliable",
                "cmd: capabilities.exit_code == unreliable");
            Assert(cmd.Output.InputEchoStrategy == "deterministic_byte_match",
                "cmd: output.input_echo_strategy == deterministic_byte_match");
            Assert(cmd.Capabilities.UserBusyDetection == "process_polling",
                "cmd: capabilities.user_busy_detection == process_polling");
            Assert(cmd.Input.ClearLine == null,
                "cmd: input.clear_line is null (cooked-mode, no line editor)");
        }

        // REPL adapters that deliberately run without a line editor
        // (Python basic REPL, fsi --readline-, Racket -i, CCL, ABCL)
        // MUST have clear_line == null. Shipping "\x01\x0b" to them
        // produces SyntaxError: invalid non-printable character U+0001
        // because their parsers receive raw bytes. Regression guard.
        foreach (var name in new[] { "python", "fsi", "racket", "ccl", "abcl" })
        {
            var adapter = registry.Find(name);
            if (adapter != null)
            {
                Assert(adapter.Input.ClearLine == null,
                    $"{name}: input.clear_line is null (no line editor — raw bytes reach the parser)");
            }
        }

        // Alias test: "powershell" should resolve to the pwsh adapter.
        var aliased = registry.Find("powershell");
        Assert(aliased != null && aliased.Name == "pwsh",
            "alias: 'powershell' resolves to pwsh");

        // External loading: write a YAML to a temp directory and verify
        // AdapterLoader.LoadFromDirectory picks it up with its
        // integration_script populated from an inline block.
        var tempDir = Path.Combine(Path.GetTempPath(), $"ripple-adapter-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var testAdapterPath = Path.Combine(tempDir, "testshell.yaml");
            File.WriteAllText(testAdapterPath, """
schema: 1
name: testshell
version: 0.0.1
description: ephemeral unit-test adapter
family: shell
process:
  command_template: '"{shell_path}"'
  inherit_environment: true
ready:
  wait_for_event: prompt_start
init:
  strategy: none
  delivery: none
output:
  post_prompt_settle_ms: 25
input:
  line_ending: "\n"
signals:
  interrupt: "\u0003"
lifecycle:
  shutdown:
    command: exit
capabilities:
  stateful: true
integration_script: |
  # inline integration script body for a test adapter
  echo 'testshell integration loaded'
""");

            var extResult = AdapterLoader.LoadFromDirectory(tempDir);
            Assert(extResult.Errors.Count == 0,
                $"external: no parse errors (got {extResult.Errors.Count})");
            Assert(extResult.Adapters.Count == 1,
                $"external: exactly one adapter loaded (got {extResult.Adapters.Count})");
            if (extResult.Adapters.Count == 1)
            {
                var loaded = extResult.Adapters[0];
                Assert(loaded.Source == AdapterLoader.AdapterSource.External,
                    "external: source marked External");
                Assert(loaded.Origin == testAdapterPath,
                    $"external: origin is the YAML file path (got {loaded.Origin})");
                Assert(loaded.Adapter.Name == "testshell",
                    $"external: adapter name round-trips (got {loaded.Adapter.Name})");
                Assert(loaded.Adapter.IntegrationScript?.Contains("testshell integration loaded") == true,
                    "external: inline integration_script block preserved");
            }

            // File-based script_resource: drop an integration.sh next to
            // the YAML and reference it from a second adapter.
            var scriptPath = Path.Combine(tempDir, "integration.testsh");
            File.WriteAllText(scriptPath, "# file-based external integration script\necho 'loaded from file'\n");
            var secondAdapterPath = Path.Combine(tempDir, "filebacked.yaml");
            File.WriteAllText(secondAdapterPath, """
schema: 1
name: filebacked
version: 0.0.1
description: adapter whose integration script comes from a sibling file
family: shell
process:
  command_template: '"{shell_path}"'
  inherit_environment: true
ready:
  wait_for_event: prompt_start
init:
  strategy: shell_integration
  delivery: pty_inject
  script_resource: integration.testsh
output:
  post_prompt_settle_ms: 25
input:
  line_ending: "\n"
signals:
  interrupt: "\u0003"
lifecycle:
  shutdown:
    command: exit
capabilities:
  stateful: true
""");

            var extResult2 = AdapterLoader.LoadFromDirectory(tempDir);
            var filebacked = extResult2.Adapters
                .FirstOrDefault(a => a.Adapter.Name == "filebacked");
            Assert(filebacked != null,
                "external: file-backed adapter parses");
            Assert(filebacked?.Adapter.IntegrationScript?.Contains("loaded from file") == true,
                "external: script_resource resolved relative to YAML directory");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
