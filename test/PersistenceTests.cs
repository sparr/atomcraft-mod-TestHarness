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
}
