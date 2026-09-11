using System.Collections;
using Atomcraft;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// The load half of persistence, which had no direct coverage at all.
///
/// Everything here would have passed before the universe cache was cleared in SaveAndReload,
/// because nothing asserted that a load actually happened: FileManager returned the universe
/// it already had in memory, TryLoadUniverseFile was never called, and the mod loader's
/// OnUniverseLoad never ran. A full run showed 22 saves and 0 loads.
///
/// So these tests are written to fail if the load path stops running, rather than to check
/// that values came back, which they would appear to do either way.
/// </summary>
public static class LoadPathTests
{
    /// <summary>
    /// The mod loader's load hook fires, and receives what the save hook returned.
    ///
    /// The most direct statement of the thing that was broken. A mod's entire persistence
    /// story hangs off this one call.
    /// </summary>
    [GameTest]
    public static IEnumerator TheModLoadHookFiresWithWhatWasSaved()
    {
        yield return Session.Enter("flat");

        const int x = 2500;
        var y = WorldFixtures.GroundY - 12;
        var channel = FieldRegistry.Get<int>(ExampleChannel.Name);
        channel.Put(x, y, 4242);

        var callsBefore = ModEntry.LoadHookCalls;

        yield return Session.SaveAndReload();

        if (ModEntry.LoadHookCalls == callsBefore)
            throw new AssertionException(
                "OnUniverseLoad never fired across a save and reload, so nothing in this suite " +
                "is testing a mod's load path. Check that Session.SaveAndReload still clears " +
                "FileManager's universe cache.");

        var payload = ModEntry.LastLoadPayload;
        if (payload == null || payload.Count == 0)
            throw new AssertionException(
                "the load hook fired but was handed nothing, so the save hook's return value " +
                "did not reach it");

        if (channel.At(x, y) != 4242)
            throw new AssertionException(
                $"the channel was restored to {channel.At(x, y)} rather than 4242");

        yield return Session.Leave();
    }

    /// <summary>
    /// A second reload loads again. One load could be an artifact of the first entry rather
    /// than of the reload, and the counter would not tell the difference.
    /// </summary>
    [GameTest]
    public static IEnumerator EveryReloadReadsTheFileAgain()
    {
        yield return Session.Enter("flat");

        var first = Session.UniverseLoads;
        yield return Session.SaveAndReload();
        var second = Session.UniverseLoads;
        yield return Session.SaveAndReload();
        var third = Session.UniverseLoads;

        if (second <= first || third <= second)
            throw new AssertionException(
                $"expected a file read per reload, got {first} then {second} then {third}");

        yield return Session.Leave();
    }

    /// <summary>
    /// State does not leak from one world into another.
    ///
    /// This is the scenario that makes clearing mod state on world end necessary, and the one
    /// a save-and-reload of a single world cannot exercise: leaving a compressed base and
    /// starting a new world must not carry the old world's state into it. A mod that persists
    /// correctly and never clears looks perfect until someone plays two worlds.
    ///
    /// The harness does not clear a mod's state on world end for it, deliberately: what
    /// "clean" means is the mod's business. This test states the requirement so a mod can see
    /// what it is being asked for.
    /// </summary>
    [GameTest]
    public static IEnumerator StateDoesNotCarryFromOneWorldToAnother()
    {
        const int x = 2600;
        var y = WorldFixtures.GroundY - 12;
        var channel = FieldRegistry.Get<int>(ExampleChannel.Name);

        yield return Session.Enter("flat");
        channel.Put(x, y, 777);
        Session.Save();
        yield return Session.Leave();

        // A different name, so this is a different world rather than a reload of the first.
        yield return Session.Enter("flat", WorldMode.Creative, fresh: true,
            worldName: "TestHarnessFixtureB");

        var carried = channel.At(x, y);

        // Clean up before asserting, so a failure does not also leave a session open.
        yield return Session.Leave();
        Session.ForgetCachedUniverse();

        if (carried == 777)
            throw new AssertionException(
                "state written in one world was still present in a different world. A mod that " +
                "does not clear its state when a world ends carries it into the next one, and " +
                "every save-and-reload test still passes while it does.");
    }

    /// <summary>
    /// The harness's own guard against the failure that hid all of this: SaveAndReload must
    /// refuse to report success when no file was read.
    ///
    /// Simulated by re-entering without clearing the cache, which is exactly what the helper
    /// used to do, and asserting that the load counter does not move. If this ever stops
    /// holding, the cache behavior has changed and the guard in SaveAndReload is no longer
    /// guarding anything.
    /// </summary>
    [GameTest]
    public static IEnumerator ReEnteringWithoutClearingTheCacheReadsNothing()
    {
        yield return Session.Enter("flat");
        Session.Save();
        yield return Session.Leave();

        var before = Session.UniverseLoads;
        yield return Session.Enter("flat", WorldMode.Creative, fresh: false);
        var after = Session.UniverseLoads;

        yield return Session.Leave();
        Session.ForgetCachedUniverse();

        if (after != before)
            throw new AssertionException(
                $"re-entering with the universe still cached read the file anyway ({before} -> " +
                $"{after}). The cache no longer shadows the load path, so SaveAndReload's " +
                "ForgetCachedUniverse call and its guard may both be obsolete.");
    }
}
