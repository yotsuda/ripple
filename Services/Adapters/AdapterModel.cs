namespace Splash.Services.Adapters;

/// <summary>
/// In-memory representation of a splash adapter YAML (schema v1).
/// Mirrors adapters/SCHEMA.md. Optional sections are nullable.
///
/// This is the data model only — loading is in AdapterLoader,
/// lookup is in AdapterRegistry, and consumption is in ConsoleWorker
/// (once phase B replaces hardcoded shell branches).
/// </summary>
public class Adapter
{
    public int Schema { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Homepage { get; set; }
    public string? License { get; set; }
    public string Family { get; set; } = "";          // shell | repl | debugger
    public List<string>? Aliases { get; set; }

    public ProcessSpec Process { get; set; } = new();
    public ReadySpec Ready { get; set; } = new();
    public InitSpec Init { get; set; } = new();
    public PromptSpec Prompt { get; set; } = new();
    public OutputSpec Output { get; set; } = new();
    public InputSpec Input { get; set; } = new();
    public List<ModeSpec>? Modes { get; set; }
    public CommandsSpec? Commands { get; set; }
    public SignalsSpec Signals { get; set; } = new();
    public LifecycleSpec Lifecycle { get; set; } = new();
    public CapabilitiesSpec Capabilities { get; set; } = new();
    public ProbeSpec? Probe { get; set; }
    public List<AdapterTest>? Tests { get; set; }

    /// <summary>
    /// Inline integration script body. When present, takes precedence over
    /// process.script_resource. For pwsh/bash/zsh in the v1 draft this is
    /// the full content of ShellIntegration/integration.{ps1,bash,zsh}.
    /// </summary>
    public string? IntegrationScript { get; set; }
}

public class ProcessSpec
{
    public string CommandTemplate { get; set; } = "";
    public string? PromptTemplate { get; set; }       // cmd's /k PROMPT payload
    public bool InheritEnvironment { get; set; } = true;
    public Dictionary<string, string>? Env { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public string LineEnding { get; set; } = "\n";

    /// <summary>
    /// Optional override: the actual binary to launch when this adapter
    /// is selected, distinct from the adapter's <c>name</c>. Used by
    /// adapters where the user-facing REPL name does not match an
    /// executable on PATH — e.g. <c>fsi</c> (the F# Interactive REPL)
    /// is launched via <c>dotnet fsi</c>, not a standalone fsi.exe. The
    /// worker re-resolves <c>{shell_path}</c> against this override
    /// before expanding <see cref="CommandTemplate"/>. When null, the
    /// default behaviour applies: the shell name is resolved verbatim.
    /// </summary>
    public string? Executable { get; set; }
}

public class ReadySpec
{
    public string WaitForEvent { get; set; } = "prompt_start";
    public string? WaitFor { get; set; }              // regex fallback
    public int TimeoutMs { get; set; }
    public int SettleBeforeInjectMs { get; set; }
    public bool SuppressMirrorDuringInject { get; set; }
    public bool KickEnterAfterReady { get; set; }
    public int DelayAfterInjectMs { get; set; }

    // WaitForOutputSettled tuning — see ConsoleWorker.WaitForOutputSettled.
    // Defaults match the pre-schema hardcoded values so omitting these in
    // existing adapters is a no-op.
    public int OutputSettledMinMs { get; set; } = 2000;
    public int OutputSettledStableMs { get; set; } = 1000;
    public int OutputSettledMaxMs { get; set; } = 30000;
}

public class InitSpec
{
    public string Strategy { get; set; } = "none";    // shell_integration | marker | prompt_variable | regex | none
    public string HookType { get; set; } = "none";    // prompt_function | preexec | ps0 | precommand_lookup_action | debug_trap | custom | none
    public string Delivery { get; set; } = "none";    // launch_command | pty_inject | none
    public string? ScriptResource { get; set; }
    public string? Script { get; set; }
    public string? InitInvocationTemplate { get; set; }
    public int WaitAfterMs { get; set; }

    public TempfileSpec? Tempfile { get; set; }
    public BannerInjectionSpec? BannerInjection { get; set; }
    public InjectSpec? Inject { get; set; }
    public MarkerSpec? Marker { get; set; }
}

public class TempfileSpec
{
    public string? Prefix { get; set; }
    public string? Extension { get; set; }
    public string? PathTemplate { get; set; }
    public string? InvocationTemplate { get; set; }
    public string? HistoryFilter { get; set; }
    public bool CleanupOnStart { get; set; }
    public int StaleTtlHours { get; set; }
}

public class BannerInjectionSpec
{
    public string Mode { get; set; } = "none";        // prepend_to_tempfile | write_before_pty | none
    public string? BannerTemplate { get; set; }
    public string? ReasonTemplate { get; set; }
}

public class InjectSpec
{
    public string Method { get; set; } = "";          // source_file | heredoc_eval
    public InjectWindowsSpec? Windows { get; set; }
    public InjectUnixSpec? Unix { get; set; }
}

public class InjectWindowsSpec
{
    public string? TempfilePrefix { get; set; }
    public string? TempfileExtension { get; set; }
    public string? WslPathTemplate { get; set; }
    public string? MsysPathTemplate { get; set; }
    public string? SourceCommandTemplate { get; set; }
}

public class InjectUnixSpec
{
    public string? TempfilePathTemplate { get; set; }
    public string? SourceCommandTemplate { get; set; }
}

public class MarkerSpec
{
    public string Primary { get; set; } = "";
    public string? Continuation { get; set; }
}

public class PromptSpec
{
    public string Strategy { get; set; } = "shell_integration";  // shell_integration | marker | regex
    public ShellIntegrationSpec? ShellIntegration { get; set; }
    public string? Primary { get; set; }                         // regex for marker / regex strategies
    public string? PrimaryRegex { get; set; }
    public string? Continuation { get; set; }
    public string Anchor { get; set; } = "line_start";
    public List<GroupCapture>? GroupCaptures { get; set; }
}

public class ShellIntegrationSpec
{
    public string Protocol { get; set; } = "osc633";
    public Osc633Markers? Markers { get; set; }
    public PropertyUpdates? PropertyUpdates { get; set; }
}

public class Osc633Markers
{
    public string? PromptStart { get; set; }
    public string? CommandInputStart { get; set; }
    public string? CommandExecuted { get; set; }
    public string? CommandFinished { get; set; }
    public string? PropertyUpdate { get; set; }
}

public class PropertyUpdates
{
    public string CwdKey { get; set; } = "Cwd";
}

public class GroupCapture
{
    public string Name { get; set; } = "";
    public int Group { get; set; }
    public string Type { get; set; } = "string";     // int | string | bool
    public string? Role { get; set; }                // monotonic_counter | nesting_level | node_name | mode_indicator
}

public class OutputSpec
{
    public int PostPromptSettleMs { get; set; } = 150;
    public bool StripAnsi { get; set; }
    public bool StripInputEcho { get; set; } = true;
    public bool StripPromptEcho { get; set; } = true;
    public string InputEchoStrategy { get; set; } = "osc_boundaries"; // osc_boundaries | deterministic_byte_match | none
    public string LineEnding { get; set; } = "\n";
    public AsyncInterleaveSpec? AsyncInterleave { get; set; }
}

public class AsyncInterleaveSpec
{
    public string Strategy { get; set; } = "none";    // redraw_detect | quiesce | accept | none
    public string CaptureAs { get; set; } = "merge";  // out_of_band | merge | discard
}

public class InputSpec
{
    public string LineEnding { get; set; } = "\n";
    public string MultilineDetect { get; set; } = "none";      // prompt_based | wrapper | balanced_parens | indent_based | none
    public string MultilineDelivery { get; set; } = "direct";  // direct | tempfile | heredoc | wrapper
    public MultilineWrapperSpec? MultilineWrapper { get; set; }
    public BalancedParensSpec? BalancedParens { get; set; }
    public TempfileSpec? Tempfile { get; set; }
    public int ChunkDelayMs { get; set; }
}

public class MultilineWrapperSpec
{
    public string Open { get; set; } = "";
    public string Close { get; set; } = "";
    public string Trigger { get; set; } = "auto";    // auto | always | never
}

public class BalancedParensSpec
{
    public List<string>? Open { get; set; }
    public List<string>? Close { get; set; }
    public List<string>? StringDelims { get; set; }
    public string? Escape { get; set; }
    public string? LineComment { get; set; }
    public List<string>? BlockComment { get; set; }

    /// <summary>
    /// Reader-macro char literal prefix (e.g. Racket's <c>#\</c>).
    /// The character immediately after this prefix is consumed
    /// verbatim and never contributes to bracket depth, so
    /// <c>#\(</c> is treated as a literal open paren token rather
    /// than an unclosed bracket. Schema §18 Q1 extension.
    /// </summary>
    public string? CharLiteralPrefix { get; set; }

    /// <summary>
    /// Reader-macro datum comment prefix (e.g. Racket's <c>#;</c>).
    /// The next balanced expression after this prefix is skipped
    /// entirely — brackets inside are still balanced, but the
    /// whole sub-expression does not contribute to the outer
    /// depth count. Schema §18 Q1 extension.
    /// </summary>
    public string? DatumCommentPrefix { get; set; }
}

public class ModeSpec
{
    public string Name { get; set; } = "";
    public string? Primary { get; set; }
    public bool Default { get; set; }
    public string? EnterKey { get; set; }
    public string? ExitKey { get; set; }
    public bool AutoEnter { get; set; }
    public string? Detect { get; set; }
    public bool Nested { get; set; }
    public int? LevelCapture { get; set; }
    public List<ExitCommandSpec>? ExitCommands { get; set; }
    public string? ExitDetect { get; set; }
    public List<GroupCapture>? GroupCaptures { get; set; }
}

public class ExitCommandSpec
{
    public string Command { get; set; } = "";
    public string Effect { get; set; } = "";          // return_to_toplevel | invoke_restart | resume | unwind_one_level
}

public class CommandsSpec
{
    public string Prefix { get; set; } = "";
    public List<string>? Scope { get; set; }
    public string? Discovery { get; set; }
    public List<BuiltinCommand>? Builtin { get; set; }
}

public class BuiltinCommand
{
    public string Name { get; set; } = "";
    public string Syntax { get; set; } = "";
    public string Description { get; set; } = "";
}

public class SignalsSpec
{
    // Nullable: groovy and any future adapter whose host has a
    // destructive Ctrl-C handler (kills the process instead of
    // interrupting the running command) declares this as null so
    // AI clients and the runtime know there is no safe byte to
    // send for interrupt. The common case is still "\x03" — the
    // YAML default `interrupt: "\x03"` keeps shell/REPL adapters
    // that support cooperative interrupt concise.
    public string? Interrupt { get; set; } = "\x03";
    public string? Eof { get; set; } = "\x04";
    public string? Suspend { get; set; }
    public string? InterruptConfirm { get; set; }
}

public class LifecycleSpec
{
    public int ReadyTimeoutMs { get; set; }
    public ShutdownSpec Shutdown { get; set; } = new();
    public List<string>? RestartOn { get; set; }
}

public class ShutdownSpec
{
    public string Command { get; set; } = "exit";
    public int GraceMs { get; set; } = 1000;
    public string ForceSignal { get; set; } = "kill";
}

public class CapabilitiesSpec
{
    public bool Stateful { get; set; } = true;
    public bool Interrupt { get; set; }
    public bool MetaCommands { get; set; }
    public bool AutoModes { get; set; }
    public bool AsyncOutput { get; set; }

    /// <summary>
    /// true | false | unreliable. "unreliable" means always reports 0
    /// regardless of actual exit status (cmd.exe's PROMPT limitation).
    /// </summary>
    public string ExitCode { get; set; } = "false";

    public bool CwdTracking { get; set; }

    /// <summary>
    /// Shape of the cwd strings this adapter's shell reports.
    ///
    /// <c>windows_native</c> — Windows-style absolute paths
    /// (<c>C:\foo</c>). These can be handed to CreateProcess's
    /// <c>lpCurrentDirectory</c> parameter directly, so splash can
    /// spawn a replacement console straight into a cached dead-cwd.
    /// pwsh / powershell / cmd / python-on-Windows / node-on-Windows.
    ///
    /// <c>posix</c> — POSIX paths (<c>/mnt/c/foo</c>, <c>/home/u</c>).
    /// Not valid as a Win32 working directory. splash injects a
    /// <c>cd</c> preamble at the command level instead. bash / zsh
    /// under WSL, MSYS2, Git Bash.
    ///
    /// <c>none</c> — adapter does not track cwd at all.
    /// </summary>
    public string CwdFormat { get; set; } = "none";

    public bool JobControl { get; set; }
    public string? ShellIntegration { get; set; }
    public string? UserBusyDetection { get; set; }   // osc_b | process_polling | none
    public UserBusyDetectionParams? UserBusyDetectionParams { get; set; }
}

public class UserBusyDetectionParams
{
    public int PollIntervalMs { get; set; }
    public int CpuBusyThresholdMs { get; set; }
    public bool IncludeChildren { get; set; }
}

public class ProbeSpec
{
    public string Eval { get; set; } = "";
    public string Expect { get; set; } = "";
}

public class AdapterTest
{
    public string Name { get; set; } = "";
    public string? Setup { get; set; }
    public string? Eval { get; set; }
    public string? Expect { get; set; }
    public bool ExpectError { get; set; }
    public int? ExpectExitCode { get; set; }
    public bool ExpectCwdUpdate { get; set; }
    public string? ExpectMode { get; set; }
    public int? ExpectLevel { get; set; }
    public int? ExpectCounter { get; set; }
    public string? ExpectOutOfBand { get; set; }
    public bool ExitCodeIsUnreliable { get; set; }
    public List<AdapterTest>? SetupSequence { get; set; }
    public string? ThenEval { get; set; }
    public int WaitMs { get; set; }
}
