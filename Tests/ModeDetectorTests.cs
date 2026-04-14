using Splash.Services;
using Splash.Services.Adapters;

namespace Splash.Tests;

public static class ModeDetectorTests
{
    public static void Run()
    {
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        Console.WriteLine("=== ModeDetector Tests ===");

        // --- empty / no modes ---

        {
            var match = ModeDetector.Detect(null, "anything");
            Assert(match.Name == null && match.Level == null,
                "null modes list returns Name=null");
        }

        {
            var match = ModeDetector.Detect(new List<ModeSpec>(), "anything");
            Assert(match.Name == null,
                "empty modes list returns Name=null");
        }

        // --- single default mode, no auto_enter ---

        var singleMode = new List<ModeSpec>
        {
            new() { Name = "main", Default = true, Primary = "^>>> $" },
        };

        Assert(ModeDetector.Detect(singleMode, ">>> ").Name == "main",
            "single default mode resolves to its name regardless of input");

        Assert(ModeDetector.Detect(singleMode, "completely unrelated").Name == "main",
            "single default mode is reported even when nothing matches");

        // --- python-style main + pdb auto_enter ---

        var pythonModes = new List<ModeSpec>
        {
            new() { Name = "main", Default = true, Primary = "^>>> $" },
            new() { Name = "pdb",  AutoEnter = true, Detect = @"^\(Pdb\) $", Primary = @"^\(Pdb\) $" },
        };

        Assert(ModeDetector.Detect(pythonModes, ">>> ").Name == "main",
            "python: top-level prompt resolves to main mode");

        Assert(ModeDetector.Detect(pythonModes, "(Pdb) ").Name == "pdb",
            "python: (Pdb) prompt resolves to pdb mode");

        Assert(ModeDetector.Detect(pythonModes, "some output\n(Pdb) ").Name == "pdb",
            "python: (Pdb) prompt at end-of-output resolves to pdb mode");

        Assert(ModeDetector.Detect(pythonModes, "(Pdb) \n>>> ").Name == "pdb",
            "python: (Pdb) anywhere in output triggers pdb (auto_enter wins over default)");

        Assert(ModeDetector.Detect(pythonModes, "no prompt here").Name == "main",
            "python: no prompt match falls back to default (main)");

        // --- nested modes with level capture (SBCL debugger style) ---

        var sbclModes = new List<ModeSpec>
        {
            new() { Name = "main", Default = true, Primary = "^\\* $" },
            new()
            {
                Name = "debugger",
                AutoEnter = true,
                Nested = true,
                Detect = @"^(\d+)\] $",
                Primary = @"^(\d+)\] $",
                LevelCapture = 1,
            },
        };

        {
            var match = ModeDetector.Detect(sbclModes, "0] ");
            Assert(match.Name == "debugger" && match.Level == 0,
                $"sbcl: level 0 debugger detected (got name={match.Name}, level={match.Level})");
        }

        {
            var match = ModeDetector.Detect(sbclModes, "2] ");
            Assert(match.Name == "debugger" && match.Level == 2,
                $"sbcl: level 2 debugger detected (got name={match.Name}, level={match.Level})");
        }

        {
            var match = ModeDetector.Detect(sbclModes, "* ");
            Assert(match.Name == "main" && match.Level == null,
                $"sbcl: top-level returns main without a level (got name={match.Name})");
        }

        // --- declaration order respected for multiple auto_enter modes ---

        var multiAuto = new List<ModeSpec>
        {
            new() { Name = "main", Default = true, Primary = ">>> " },
            new() { Name = "pry",  AutoEnter = true, Detect = "^pry> $" },
            new() { Name = "pdb",  AutoEnter = true, Detect = @"^\(Pdb\) $" },
        };

        Assert(ModeDetector.Detect(multiAuto, "pry> ").Name == "pry",
            "multi-auto: first matching auto_enter mode wins (pry)");

        Assert(ModeDetector.Detect(multiAuto, "(Pdb) ").Name == "pdb",
            "multi-auto: second matching auto_enter mode also resolves correctly");

        // --- malformed regex is skipped, not crashing ---

        var brokenMode = new List<ModeSpec>
        {
            new() { Name = "main", Default = true },
            new() { Name = "broken", AutoEnter = true, Detect = "[unclosed" },
        };
        Assert(ModeDetector.Detect(brokenMode, "anything").Name == "main",
            "malformed regex is skipped and falls through to default");

        // --- first mode is fallback when no `default: true` is set ---

        var noDefault = new List<ModeSpec>
        {
            new() { Name = "alpha", Primary = "alpha> " },
            new() { Name = "beta",  Primary = "beta> " },
        };
        Assert(ModeDetector.Detect(noDefault, "no match").Name == "alpha",
            "no default flag: first mode in declaration order is the fallback");

        Console.WriteLine($"\n{pass} passed, {fail} failed");
    }
}
