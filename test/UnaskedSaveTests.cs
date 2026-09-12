using System.Collections;
using Atomcraft;
using Godot;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// A mod calling the game's save API directly gets its save suppressed, and is told so.
///
/// Without the warning the only evidence is an absence: nothing written, no error, and a test
/// that wonders why its reload found nothing. The game's own saves, at dawn and on the way out
/// of a session, are suppressed quietly because nobody wrote them expecting an effect.
/// </summary>
public static class UnaskedSaveTests
{
    [GameTest]
    public static IEnumerator ADirectSaveCallIsRefusedAndTheCallerNamed()
    {
        yield return Session.Enter("flat");

        FileManager.SaveGame(Simulation.CurrentState, forceSaveAllSegments: true);

        var caller = Session.LastUnaskedSaveCaller;

        yield return Session.Leave();

        if (caller != "TestHarness.Test")
            throw new AssertionException(
                $"expected the refusal to name this test mod, got '{caller ?? "nothing"}'. " +
                "A mod whose save is silently dropped has no way to find out.");
    }

    /// <summary>
    /// The scope is the documented way through, and it has to actually work or the warning is
    /// telling people to do something useless.
    /// </summary>
    [GameTest]
    public static IEnumerator AllowSavesLetsADirectCallThrough()
    {
        yield return Session.Enter("flat");

        var path = ProjectSettings.GlobalizePath($"user://Worlds/{Session.CurrentWorldName}.universe");

        using (Session.AllowSaves())
            FileManager.SaveGame(Simulation.CurrentState, forceSaveAllSegments: true);

        var written = System.IO.File.Exists(path);

        yield return Session.Leave();

        if (!written)
            throw new AssertionException(
                $"AllowSaves did not let the save reach disk; nothing at {path}");
    }

    /// <summary>
    /// And the scope leaves the world reloadable, which is the point of setting PlanetWritten:
    /// a test that saves this way should not then be told its world was never saved.
    /// </summary>
    [GameTest]
    public static IEnumerator AWorldSavedThroughTheScopeCanBeReloaded()
    {
        yield return Session.Enter("flat");

        Session.SetPixel(2400, WorldFixtures.GroundY - 5, "Iron");

        using (Session.AllowSaves())
            FileManager.SaveGame(Simulation.CurrentState, forceSaveAllSegments: true);

        yield return Session.Reload();

        if (Simulation.CurrentState.Field.Get(2400, WorldFixtures.GroundY - 5) != "Iron".ToMaterialTypeId())
            throw new AssertionException("the pixel saved through the scope did not survive a reload");

        yield return Session.Leave();
    }
}
