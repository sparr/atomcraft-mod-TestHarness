using System.Collections;
using System.Linq;
using Atomcraft;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// The session lifecycle itself. If entering a fixture world does not work, every test that
/// needs saving, loading, or inventories is untestable, so these come first.
/// </summary>
public static class SessionTests
{
    [GameTest]
    public static IEnumerator AFixtureWorldCanBeEnteredAndLeft()
    {
        yield return Session.Enter("flat");

        if (!Session.Active)
            throw new AssertionException("session did not start");
        if (Game.SaveData_World?.Name != Session.WorldName)
            throw new AssertionException($"unexpected world: {Game.SaveData_World?.Name}");

        yield return Session.Leave();

        if (Session.Active)
            throw new AssertionException("session did not end");
    }

    /// <summary>
    /// The fixture's floor is where it was asked to be, which proves the substituted
    /// generator ran rather than the game's own.
    /// </summary>
    [GameTest]
    public static IEnumerator TheFixtureWorldIsFlatAndEmpty()
    {
        yield return Session.Enter("flat");

        var field = Simulation.CurrentState.Field;
        var granite = Materials.GetBaseMaterialId("Granite");
        var x = field.Width / 2;

        if (field.Get(x, WorldFixtures.GroundY) != granite)
            throw new AssertionException(
                $"expected the fixture floor at y={WorldFixtures.GroundY}, found " +
                $"{field.Get(x, WorldFixtures.GroundY).ToMaterialName() ?? "air"}");
        if (field.Get(x, WorldFixtures.GroundY - 40) != -1)
            throw new AssertionException("expected air above the fixture floor");

        yield return Session.Leave();
    }

    /// <summary>
    /// The real simulation runs, using the game's own DoSimTick and therefore its parallel
    /// quadrant passes over active chunks, which Region.Ticks deliberately avoids.
    /// </summary>
    [GameTest]
    public static IEnumerator TheRealSimulationAdvances()
    {
        yield return Session.Enter("flat");

        var before = Simulation.CurrentState.Tick;
        Session.WorldTicks(10);
        var after = Simulation.CurrentState.Tick;

        if (after != before + 10)
            throw new AssertionException($"expected tick {before + 10}, got {after}");

        yield return Session.Leave();
    }

    /// <summary>
    /// Pixels written into a world are still there after saving, leaving, and loading it
    /// back. This is the round trip anything persisted has to survive, and it exercises the
    /// game's own segment save and load rather than a harness shortcut.
    /// </summary>
    [GameTest]
    public static IEnumerator WorldContentSurvivesSaveAndReload()
    {
        yield return Session.Enter("flat");

        // Just above the fixture floor, and deliberately not uniform: the game skips
        // segments it considers uniform, so a solid block might never be written.
        const int x0 = 3000;
        var y0 = WorldFixtures.GroundY - 6;
        var pattern = new[] { "Granite", "Sand", "Iron", "Granite", "Iron", "Sand" };

        for (var i = 0; i < pattern.Length; i++)
            Session.SetPixel(x0 + i, y0, pattern[i]);

        yield return Session.SaveAndReload();

        var reloaded = Simulation.CurrentState.Field;
        for (var i = 0; i < pattern.Length; i++)
        {
            var actual = reloaded.Get(x0 + i, y0).ToMaterialName() ?? "air";
            if (actual != pattern[i])
                throw new AssertionException(
                    $"({x0 + i},{y0}) was {pattern[i]} before the save and {actual} after the reload");
        }

        yield return Session.Leave();
    }

    /// <summary>
    /// Material names survive a round trip even though ids do not. Ids are assigned by load
    /// order, so a mod's pixel comes back as a different material whenever the set of
    /// installed mods changes; a test that recorded ids would pass today and lie tomorrow.
    /// </summary>
    [GameTest]
    public static IEnumerator MaterialIdentityIsStableAcrossAReload()
    {
        yield return Session.Enter("flat");

        const int x = 3100;
        var y = WorldFixtures.GroundY - 6;
        const string material = "Iron";

        Session.SetPixel(x, y, material);

        yield return Session.SaveAndReload();

        var nameAfter = Simulation.CurrentState.Field.Get(x, y).ToMaterialName();
        if (nameAfter != material)
            throw new AssertionException(
                $"expected {material} after the reload, found {nameAfter ?? "air"}");

        yield return Session.Leave();
    }

    /// <summary>
    /// Every material in the neighbourhood is unchanged after a save and reload, not merely
    /// the cells the test wrote.
    ///
    /// Checking only your own pixels cannot catch a round trip that loses heat, drops a
    /// boundary cell, or writes a segment back at the wrong offset. The oracle is the
    /// game's own chunk checksum, which covers material and heat together and is what
    /// multiplayer uses to detect desync.
    /// </summary>
    [GameTest]
    public static IEnumerator TheWorldIsUnchangedByASaveAndReload()
    {
        yield return Session.Enter("flat");

        // A scene varied enough that a checksum means something: several materials, a heat
        // gradient, and cells left as air, spread across more than one chunk.
        const int x0 = 2600;
        var y0 = WorldFixtures.GroundY - 40;
        var palette = new[] { "Granite", "Sand", "Iron", "Water", "Compost" };

        for (var i = 0; i < 140; i++)
        {
            var x = x0 + i;
            Session.SetPixel(x, y0 + i % 7, palette[i % palette.Length]);
            Simulation.CurrentState.Field.SetHeatmap(x, y0 + i % 7, (short)(300 + i * 7));
        }

        var before = Session.ChunkMaterialChecksums(x0 - 8, y0 - 8, 160, 80);
        if (before.Count < 4)
            throw new AssertionException($"expected several chunks to compare, got {before.Count}");

        yield return Session.SaveAndReload();

        var after = Session.ChunkMaterialChecksums(x0 - 8, y0 - 8, 160, 80);

        var differing = before.Where(kv => !after.TryGetValue(kv.Key, out var v) || v != kv.Value)
                              .Select(kv => $"chunk ({kv.Key.X},{kv.Key.Y}): {kv.Value} -> " +
                                            (after.TryGetValue(kv.Key, out var a) ? a.ToString() : "missing"))
                              .ToList();

        if (differing.Count > 0)
            throw new AssertionException(
                $"{differing.Count} of {before.Count} chunks changed across the round trip:\n  " +
                string.Join("\n  ", differing.Take(6)));

        Log.Info($"save and reload left all {before.Count} chunks identical");
    }

    /// <summary>
    /// Isolates which half of a chunk checksum moves across a round trip: the materials or
    /// the heat. The game's checksum sums both, so a mismatch alone does not say which.
    /// </summary>
    [GameTest]
    public static IEnumerator WhatSurvivesASaveAndReload()
    {
        yield return Session.Enter("flat");

        const int x = 2400;
        var y = WorldFixtures.GroundY - 30;
        var field = Simulation.CurrentState.Field;

        Session.SetPixel(x, y, "Iron");
        field.SetHeatmap(x, y, 800);
        Session.SetPixel(x + 1, y, "Granite");
        field.SetHeatmap(x + 1, y, 450);

        var matBefore = (field.Get(x, y), field.Get(x + 1, y));
        var heatBefore = (field.GetHeatmap(x, y), field.GetHeatmap(x + 1, y));
        var untouchedHeatBefore = field.GetHeatmap(x + 40, y);

        yield return Session.SaveAndReload();

        var after = Simulation.CurrentState.Field;
        var matAfter = (after.Get(x, y), after.Get(x + 1, y));
        var heatAfter = (after.GetHeatmap(x, y), after.GetHeatmap(x + 1, y));
        var untouchedHeatAfter = after.GetHeatmap(x + 40, y);

        Log.Event("round_trip", new()
        {
            ["materialsBefore"] = $"{matBefore.Item1},{matBefore.Item2}",
            ["materialsAfter"] = $"{matAfter.Item1},{matAfter.Item2}",
            ["materialsMatch"] = matBefore == matAfter,
            ["heatBefore"] = $"{heatBefore.Item1},{heatBefore.Item2}",
            ["heatAfter"] = $"{heatAfter.Item1},{heatAfter.Item2}",
            ["heatMatch"] = heatBefore == heatAfter,
            ["untouchedHeatBefore"] = (int)untouchedHeatBefore,
            ["untouchedHeatAfter"] = (int)untouchedHeatAfter,
        });

        if (matBefore != matAfter)
            throw new AssertionException(
                $"materials changed across the round trip: {matBefore} -> {matAfter}");

        // Asserting what the game currently does, not what one might expect. A saved
        // segment holds material ids only, and the load path recomputes temperature from
        // each material's DefaultTemperature or the planet's ambient at that depth. If this
        // ever starts passing, the game began persisting heat and this test should invert.
        if (heatBefore == heatAfter)
            throw new AssertionException(
                "heat survived a save and reload, which the game did not previously do. " +
                "Harness docs and any mod relying on temperature resetting need revisiting.");
    }
}
