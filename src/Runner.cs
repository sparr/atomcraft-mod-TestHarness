using Atomcraft;
using Godot;
using HarmonyLib;

namespace Atomcraft.TestHarness;

/// <summary>
/// Drives the harness from the game's own frame loop. Tests that need engine frames to
/// elapse (world load, save round-trips) can only be pumped from here, so even the
/// walking skeleton uses this path rather than running inside Game._Ready.
/// </summary>
public static class Runner
{
    private static int _frame;
    private static bool _finished;
    private static bool _started;

    /// <summary>Frames pumped so far; used to tell a one-off exception from a per-frame storm.</summary>
    public static int Frame => _frame;

    /// <summary>
    /// Godot logs unhandled exceptions from _Process every frame and applies no
    /// backpressure. A single bad per-frame hook produced a 1.3 million line godot.log in
    /// under 90 seconds during Phase 1, so the pump self-terminates rather than letting a
    /// run drown the disk and time out with no usable diagnostic.
    /// </summary>
    private const int ConsecutiveFailureLimit = 5;
    private static int _consecutiveFailures;

    public static void Pump()
    {
        if (_finished || !ModEntry.Options.Run)
            return;

        _frame++;
        if (_frame < ModEntry.Options.SettleFrames)
            return;

        try
        {
            if (!_started)
            {
                _started = true;
                if (ModEntry.Options.Diagnose)
                {
                    // Diagnostics query the game directly, so they need the same
                    // prerequisites the suite does.
                    TestExecutor.EnsureGameReady();
                    Diagnostics.ValidateInstalledMods();
                    Diagnostics.MaterialIdSpaces();
                    Diagnostics.AmbientProfile();
                }
                // The loader's report sits opaque over the whole game until someone presses
                // Continue, and in a suite run nobody will, so every headful screenshot would
                // otherwise be a picture of it.
                if (View.DismissModLoaderReport())
                    Log.Info("dismissed the mod loader report so the game is visible");

                TestExecutor.Begin(ModEntry.Options.Filter, ModEntry.Options.Exclude);
                return;
            }

            // One step per engine frame. A synchronous test finishes inside a single step;
            // a frame-driven one advances by a yield, which is what gives the game a frame
            // to load a world or write a save in between.
            if (!TestExecutor.Step())
                return;

            _finished = true;
            Quit(FinalExitCode());
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            Log.Error($"harness crashed (failure {_consecutiveFailures}/{ConsecutiveFailureLimit}): {ex}");
            if (_consecutiveFailures < ConsecutiveFailureLimit)
                return;
            Log.Event("run_end", new() { ["status"] = "crashed", ["error"] = ex.Message });
            _finished = true;
            Quit(70);
        }
    }

    private static int FinalExitCode()
    {
        EmitExceptionSummary();

        // Deliberate escape hatch for proving the wrapper propagates a failing run.
        if (ModEntry.Options.ForceExitCode is int forced)
        {
            Log.Event("run_end", new() { ["status"] = "forced", ["exitCode"] = forced });
            return forced;
        }

        if (TestExecutor.ExitCode != 0)
        {
            Log.Event("run_end", new() { ["status"] = "failed", ["exitCode"] = 1 });
            return 1;
        }

        // A run that provoked engine exceptions is not a clean run, even if every test
        // passed. Individual tests are already failed for their own; this covers the rest.
        if (ExceptionSuppressor.Any && !ModEntry.Options.AllowEngineExceptions)
        {
            Log.Event("run_end", new()
            {
                ["status"] = "engine_exceptions",
                ["exitCode"] = 72,
                ["distinct"] = ExceptionSuppressor.Summary(_frame).Count,
            });
            return 72;
        }

        Log.Event("run_end", new() { ["status"] = "ok", ["exitCode"] = 0 });
        return 0;
    }

    /// <summary>
    /// Ends the run when engine exceptions are arriving faster than they can be useful.
    /// Turns a hang plus an unbounded log into a fast failure that still carries the first
    /// full stack of every distinct signature.
    /// </summary>
    public static void AbortFromExceptionStorm()
    {
        if (_finished)
            return;
        _finished = true;
        Log.Error($"aborting: {ExceptionSuppressor.TotalSuppressed} suppressed engine exceptions "
                + $"past the threshold of {ExceptionSuppressor.AbortThreshold}");
        EmitExceptionSummary();
        Log.Event("run_end", new()
        {
            ["status"] = "exception_storm",
            ["exitCode"] = ExceptionSuppressor.StormExitCode,
        });
        Quit(ExceptionSuppressor.StormExitCode);
    }

    private static void EmitExceptionSummary()
    {
        foreach (var record in ExceptionSuppressor.Summary(_frame))
            Log.Event("engine_exception", record);
    }

    private static void Quit(int exitCode)
    {
        Log.Info($"requesting quit with exit code {exitCode}");
        ExceptionSuppressor.Context = "shutdown";
        var tree = Game.Instance?.GetTree();
        if (tree == null)
        {
            Log.Error("no SceneTree; cannot quit cleanly");
            return;
        }
        tree.Quit(exitCode);
    }
}

/// <summary>
/// Attaches the pump to <see cref="SceneTree.ProcessFrame"/> once the game has finished
/// initializing. Using the signal rather than a postfix on Game._Process keeps Harmony off
/// the hottest method in the game and needs no custom Node type.
/// </summary>
[HarmonyPatch(typeof(Game), "_Ready")]
internal static class GameReadyPatch
{
    private static void Postfix()
    {
        Log.Info("Game._Ready postfix reached; attaching frame pump");
        var tree = Game.Instance?.GetTree();
        if (tree == null)
        {
            Log.Error("no SceneTree at end of Game._Ready; cannot attach pump");
            return;
        }
        tree.ProcessFrame += Runner.Pump;
    }
}
