using System.Collections;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// The save and reload helper, proved against a channel the harness persists itself.
///
/// Without a channel that genuinely round trips, the helper could only ever be shown to
/// fail. ExampleChannel exists so the passing case is real, and it doubles as the worked
/// example a mod copies.
/// </summary>
public static class PersistenceTests
{
    [GameTest]
    public static IEnumerator APersistedModFieldSurvivesASaveAndReload()
    {
        yield return Session.Enter("flat");

        var x = 2800;
        var y = WorldFixtures.GroundY - 20;
        var field = FieldRegistry.Get<int>(ExampleChannel.Name);

        for (var i = 0; i < 10; i++)
            field.Put(x + i, y, 100 + i * 3);

        yield return Persistence.AssertFieldSurvivesSaveLoad<int>(ExampleChannel.Name, x, y, 12, 1);

        yield return Session.Leave();
    }

    /// <summary>
    /// The helper refuses a rectangle with nothing in it. A test that asserts empty state
    /// survives would pass whether or not persistence works at all, which is the failure
    /// mode most likely to be written by accident.
    /// </summary>
    [GameTest]
    public static IEnumerator TheHelperRejectsAnEmptyRectangle()
    {
        yield return Session.Enter("flat");

        var threw = false;
        var probe = Persistence.AssertFieldSurvivesSaveLoad<int>(
            ExampleChannel.Name, 2900, WorldFixtures.GroundY - 20, 4, 1);
        try
        {
            probe.MoveNext();          // the emptiness check runs before the first yield
        }
        catch (AssertionException)
        {
            threw = true;
        }

        if (!threw)
            throw new AssertionException(
                "the helper accepted a rectangle with no state in it, so it would pass " +
                "without testing anything");

        yield return Session.Leave();
    }

    /// <summary>
    /// The replaced-on-load helper, which had no test and consequently shipped for a release
    /// with the defect its sibling was fixed for: it open-coded a leave and re-entry without
    /// clearing FileManager's universe cache, so the re-entry was served from memory and the
    /// mod loader's restore hook never ran.
    ///
    /// For a mod that clears its state when a world ends, which the harness now does for every
    /// registered channel, that reported a false failure rather than a false pass, and the
    /// obvious reading of it was that the mod's save hook was broken.
    ///
    /// Non-vacuity is inherited rather than asserted here: the helper goes through
    /// Session.Reload, which fails if no file was actually read.
    /// </summary>
    [GameTest]
    public static IEnumerator StateWrittenAfterASaveIsDroppedByTheReload()
    {
        yield return Session.Enter("flat");

        var y = WorldFixtures.GroundY - 20;
        var channel = FieldRegistry.Get<int>(ExampleChannel.Name);

        for (var i = 0; i < 6; i++)
            channel.Put(2700 + i, y, 500 + i);

        // The saved rectangle must come back; the second must not, since it is written after
        // the save and a correct load has no record of it.
        yield return Persistence.AssertFieldIsReplacedOnLoad<int>(
            ExampleChannel.Name,
            savedRect: (2700, y, 6, 1),
            unsavedRect: (2720, y, 4, 1),
            marker: 999);

        yield return Session.Leave();
    }
}
