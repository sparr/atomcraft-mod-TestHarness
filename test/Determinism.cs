using System.Collections;
using System.Linq;
using Atomcraft;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Meta-tests: they check the harness's central promise rather than any game behavior.
///
/// If the same fixture stepped the same number of ticks can produce two different worlds,
/// every other test in every suite is unreliable, and the failures look like flaky physics
/// rather than like what they are.
///
/// The most likely cause in this codebase is a mod storing mutable state on a BaseMaterial
/// subclass. There is one instance per material type, shared across the whole world and
/// every simulation thread, so a field on it leaks between pixels and between tests.
/// </summary>
public static class Determinism
{
    /// <summary>Builds the same nontrivial scene every time. Deliberately busy: falling
    /// solids, flowing liquid, a heat gradient, and a reaction-capable pair.</summary>
    private static void Scene(Region r)
    {
        r.Fill(0, 40, r.Width, 1, "Granite");
        r.Fill(10, 4, 6, 3, "Sand");
        r.Fill(30, 2, 8, 4, "Water");
        r.Fill(44, 6, 4, 4, "Iron", 900);
        r.Set(20, 10, "Compost");
        r.SetHeat(32, 38, 1200);
    }

    [GameTest]
    public static void TheSameSceneStepsToTheSameWorld(Region r)
    {
        Scene(r);
        r.Ticks(120);
        var first = r.Checksum();

        r.Clear();
        r.Tick = 0;
        Scene(r);
        r.Ticks(120);
        var second = r.Checksum();

        Log.Event("determinism", new()
        {
            ["scene"] = "mixed",
            ["ticks"] = 120,
            ["checksum"] = first,
            ["repeat"] = second,
            ["processorCount"] = System.Environment.ProcessorCount,
        });

        if (first != second)
            throw new AssertionException(
                $"the same scene stepped 120 ticks twice produced different worlds: " +
                $"{first} then {second}. Something is carrying state between runs; the usual " +
                $"cause is a mutable field on a BaseMaterial subclass, which is shared across " +
                $"the whole world.\n{r.Dump()}");
    }

    /// <summary>
    /// Stepping is single-threaded and bounded to the region, so the result must not depend
    /// on how many cores the machine has. The checksum is recorded so a wrapper can compare
    /// it across runs at different --cores values; this test asserts the within-run half.
    /// </summary>
    [GameTest]
    public static void SteppingDoesNotDependOnCoreCount(Region r)
    {
        Scene(r);
        r.Ticks(60);

        Log.Event("determinism", new()
        {
            ["scene"] = "cores",
            ["ticks"] = 60,
            ["checksum"] = r.Checksum(),
            ["processorCount"] = System.Environment.ProcessorCount,
            ["dotnetProcessorCount"] = System.Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT"),
        });
    }

    /// <summary>
    /// The scene for the parallel test, built near the spaceship so the chunks holding it
    /// are active. Simulation.Step only visits active chunks, which are flagged around
    /// avatars and the spaceship, so a scene built anywhere else is never simulated and the
    /// test would compare two worlds that never moved.
    /// </summary>
    private static void WorldScene(int cx, int cy)
    {
        for (var i = 0; i < 40; i++)
        {
            Session.SetPixel(cx - 20 + i, cy - 18, "Sand");
            Session.SetPixel(cx - 20 + i, cy - 2, "Granite");
        }
        for (var i = 0; i < 12; i++)
            Session.SetPixel(cx - 6 + i, cy - 12, "Water");

        Simulation.CurrentState.Field.SetHeatmap(cx, cy - 10, 1800);
        Simulation.CurrentState.Field.SetHeatmap(cx - 10, cy - 10, 80);
    }

    /// <summary>
    /// The real simulation, run twice over identical worlds, produces identical results.
    ///
    /// This is the one that exercises what Region.Ticks deliberately avoids: DoSimTick runs
    /// Parallel.ForEach over active chunks in four quadrant passes, so a result that depends
    /// on thread scheduling shows up here and nowhere else. The checksum is the game's own,
    /// covering heat as well as material, because heat is where order-dependence would
    /// surface first.
    ///
    /// Run at more than one core count with run-tests.sh --determinism.
    /// </summary>
    [GameTest]
    public static IEnumerator TheParallelSimulationIsReproducible()
    {
        const int ticks = 90;
        var cx = 3072;                       // the fixture puts the spaceship here
        var cy = WorldFixtures.GroundY - 8;

        yield return Session.Enter("flat");
        WorldScene(cx, cy);
        Session.WorldTicks(ticks);
        var first = Session.ChunkChecksums(cx - 64, cy - 64, 192, 128);
        yield return Session.Leave();

        yield return Session.Enter("flat");
        WorldScene(cx, cy);
        Session.WorldTicks(ticks);
        var second = Session.ChunkChecksums(cx - 64, cy - 64, 192, 128);
        yield return Session.Leave();

        var differing = first.Where(kv => !second.TryGetValue(kv.Key, out var v) || v != kv.Value)
                             .Select(kv => $"chunk ({kv.Key.X},{kv.Key.Y}): {kv.Value} vs " +
                                           (second.TryGetValue(kv.Key, out var b) ? b.ToString() : "missing"))
                             .ToList();

        Log.Event("determinism", new()
        {
            ["scene"] = "parallel",
            ["ticks"] = ticks,
            ["chunks"] = first.Count,
            ["checksum"] = first.Aggregate(0, (a, kv) => a * 31 + kv.Value),
            ["processorCount"] = System.Environment.ProcessorCount,
        });

        if (differing.Count > 0)
            throw new AssertionException(
                $"the parallel simulation produced different worlds from identical starts: " +
                $"{differing.Count} of {first.Count} chunks differ\n  " +
                string.Join("\n  ", differing.Take(5)));
    }

    /// <summary>
    /// Every registered channel, the game's and any mod's, is identical after stepping the
    /// same scene twice.
    ///
    /// The game's chunk checksum covers material and heat only, so it cannot see a mod's own
    /// per-cell state, which is where order-dependence is likeliest: the material map is
    /// written under a partitioning the engine designed, while a mod's parallel structure
    /// has whatever guard rails the mod author supplied.
    /// </summary>
    [GameTest]
    public static void RegisteredChannelsStepToTheSameValues(Region r)
    {
        Scene(r);
        r.Ticks(60);
        var first = FieldRegistry.ChecksumAll(r.OriginX, r.OriginY, r.Width, r.Height);

        r.Clear();
        r.Tick = 0;
        Scene(r);
        r.Ticks(60);
        var second = FieldRegistry.ChecksumAll(r.OriginX, r.OriginY, r.Width, r.Height);

        var moved = first.Where(kv => !second.TryGetValue(kv.Key, out var v) || v != kv.Value)
                         .Select(kv => kv.Key)
                         .ToList();

        Log.Event("determinism", new()
        {
            ["scene"] = "channels",
            ["channels"] = first.Count,
            ["moved"] = moved.Count,
        });

        if (moved.Count > 0)
            throw new AssertionException(
                $"the same scene stepped twice produced different values in: {string.Join(", ", moved)}");
    }
}
