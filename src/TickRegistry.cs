using Atomcraft;

namespace Atomcraft.TestHarness;

/// <summary>
/// A mod's own per-tick pass, so a region test drives it.
///
/// Region.Ticks calls Simulation.SimulateQuadrant directly, which is what makes a region
/// test single-threaded, bounded, and reproducible. The cost is that it never reaches
/// Simulation.Step, the game's only hook for per-tick work that is not per-material. A mod
/// whose pass hangs off a Step postfix is therefore not driven by any region test at all:
/// the mod appears to do nothing, and the test fails for a reason unrelated to the mod.
///
/// Registering here fixes that without giving up the properties region tests exist for.
/// Session tests need none of this: WorldTicks drives the game's own DoSimTick, so a Step
/// postfix runs there the way it does in a real game.
/// </summary>
/// <summary>
/// Where a registered pass runs relative to the game's own per-cell passes.
///
/// The game offers the equivalent seam: Simulation.Step clears every cell's
/// updated-this-tick flag, flags the active chunks, and only then moves anything, so a mod
/// whose movement must take precedence over gravity hooks in between, while a mod that
/// reacts to where things ended up hooks after.
///
/// Which one a mod needs decides how it interacts with gravity, so it is not a detail a test
/// should inherit by accident: after gravity, a pixel pushed up out of a vent is pulled back
/// down before the pixel behind it can follow.
/// </summary>
public enum TickPhase
{
    /// <summary>
    /// After the quadrant passes, so the pass sees where the game left everything. The
    /// default, and what every pass did before this existed.
    /// </summary>
    AfterSimulation,

    /// <summary>
    /// After the updated-this-tick flags are cleared and before the quadrant passes, which is
    /// where Simulation.FlagActiveChunks sits in the real game. A pass here can move a cell
    /// and mark it with SetUpdatedWithinCurrentTick so the vanilla passes leave it alone.
    /// </summary>
    BeforeSimulation,
}

public sealed class TickSpec
{
    public required string Name { get; init; }

    /// <summary>
    /// When this pass runs. Defaults to after, which is what passes did before the choice
    /// existed, so an existing registration is unaffected.
    /// </summary>
    public TickPhase When { get; init; } = TickPhase.AfterSimulation;

    /// <summary>
    /// The pass, given the bounds it may touch and the current tick.
    ///
    /// The window is not decoration. A pass that ignores it operates world-wide while the
    /// region it is being tested in does not, so a pixel pushed past the region edge lands
    /// in the spacing margin and waits there for whichever test uses that band next.
    /// </summary>
    public required Action<RectInt, int> Step { get; init; }
}

public static class TickRegistry
{
    private static readonly List<TickSpec> Specs = new();
    private static readonly object Lock = new();

    public static void Register(TickSpec spec)
    {
        lock (Lock)
        {
            if (Specs.Any(s => s.Name == spec.Name))
                throw new AssertionException($"tick pass '{spec.Name}' is already registered");
            Specs.Add(spec);
        }
        Log.Info($"registered tick pass '{spec.Name}'");
    }

    /// <summary>
    /// Removes a pass. A mod registers once at load and never needs this; a test that
    /// registers a pass of its own does, because anything left behind runs for every test
    /// that ticks afterwards.
    /// </summary>
    public static bool Unregister(string name)
    {
        lock (Lock)
            return Specs.RemoveAll(s => s.Name == name) > 0;
    }

    public static IReadOnlyList<TickSpec> All
    {
        get { lock (Lock) return Specs.ToList(); }
    }

    /// <summary>
    /// Runs every registered pass for one phase, in registration order.
    ///
    /// A pass that throws fails the running test rather than being swallowed, because a mod
    /// whose tick pass is broken should not look like a mod whose behavior is wrong.
    /// </summary>
    public static void StepAll(RectInt window, int tick, TickPhase phase = TickPhase.AfterSimulation)
    {
        foreach (var spec in All)
        {
            if (spec.When != phase) continue;

            try
            {
                spec.Step(window, tick);
            }
            catch (Exception ex)
            {
                throw new AssertionException(
                    $"tick pass '{spec.Name}' threw at tick {tick}: {ex.Message}");
            }
        }
    }
}
