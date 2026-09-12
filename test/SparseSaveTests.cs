using System.Collections;
using Atomcraft;
using Godot;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// The first save of a generated world has to be complete.
///
/// Worldgen no longer serializes the planet's segments, so nothing has put them in the
/// universe. An incremental save writes only Simulation.DirtyPlanetSegmentOrigins, which after
/// worldgen holds just what the test touched, and the reload returns those few segments with
/// air everywhere else. The failure is quiet in the worst way: the test's own pixels are
/// exactly the ones that were saved, so an assertion about them passes while the rest of the
/// planet is gone.
/// </summary>
public static class SparseSaveTests
{
    [GameTest]
    public static IEnumerator AnIncrementalFirstSaveStillWritesTheWholePlanet()
    {
        yield return Session.Enter("flat");

        var y = WorldFixtures.GroundY;
        const int touched = 2400;
        const int untouched = 3600;    // far enough to be a different planet segment

        // Ground the fixture built, which no test wrote and so nothing marks dirty.
        var groundBefore = Simulation.CurrentState.Field.Get(untouched, y);
        if (groundBefore == -1)
            throw new AssertionException(
                $"expected fixture ground at ({untouched},{y}); without it this test cannot tell " +
                "a sparse save from a complete one");

        Session.SetPixel(touched, y - 5, "Iron");

        // Asking for incremental: the harness should override it, because this world has never
        // been written and the untouched segments exist nowhere but memory.
        yield return Session.SaveAndReload(incremental: true);

        var field = Simulation.CurrentState.Field;

        if (field.Get(untouched, y) != groundBefore)
            throw new AssertionException(
                $"ground at ({untouched},{y}) came back as {field.Get(untouched, y)} instead of " +
                $"{groundBefore}. The first save wrote only dirty segments, so every segment the " +
                "test did not touch is absent from the file and loads as air.");

        if (field.Get(touched, y - 5) != "Iron".ToMaterialTypeId())
            throw new AssertionException("the test's own pixel did not survive the round trip");

        yield return Session.Leave();
    }

    /// <summary>
    /// Once a world has been written, incremental is honored: it is correct then, and much
    /// cheaper. Without this the rule above could be "always save everything", which would give
    /// up the option entirely.
    /// </summary>
    [GameTest]
    public static IEnumerator IncrementalIsHonoredOnceTheWorldExists()
    {
        yield return Session.Enter("flat");

        Session.SetPixel(2400, WorldFixtures.GroundY - 5, "Iron");
        Session.Save();                                   // complete, by the rule above

        Session.SetPixel(2401, WorldFixtures.GroundY - 5, "Granite");
        yield return Session.SaveAndReload(incremental: true);

        var field = Simulation.CurrentState.Field;

        if (field.Get(2401, WorldFixtures.GroundY - 5) != "Granite".ToMaterialTypeId())
            throw new AssertionException("the incremental save lost the pixel it was asked to write");

        if (field.Get(3600, WorldFixtures.GroundY) == -1)
            throw new AssertionException(
                "an incremental save after a complete one lost the untouched planet");

        yield return Session.Leave();
    }

    /// <summary>
    /// Nothing reaches disk until a test asks. This is what makes "never saved" mean what the
    /// Reload guard says it means.
    /// </summary>
    [GameTest]
    public static IEnumerator GeneratingAWorldWritesNothing()
    {
        yield return Session.Enter("flat");

        var path = ProjectSettings.GlobalizePath($"user://Worlds/{Session.CurrentWorldName}.universe");
        var existed = System.IO.File.Exists(path);

        yield return Session.Leave();

        if (existed)
            throw new AssertionException(
                $"generating a world wrote {path}. Nothing should reach disk until a test asks " +
                "for a save, or a reload of a world that was never saved finds a header with no " +
                "planet segments behind it.");
    }
}
