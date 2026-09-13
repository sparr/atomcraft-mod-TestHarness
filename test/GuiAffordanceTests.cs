using System.Collections;
using Atomcraft;
using Atomcraft.TestHarness;
using Godot;

// The game has a Session type of its own; the harness's is the one meant here.
using Session = Atomcraft.TestHarness.Session;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// The GUI affordances: a headless probe for the reflection they depend on, a check that the
/// RequiresDisplay gate holds, and one end-to-end headful test built entirely on vanilla
/// content, so the affordances are validated by this suite alone rather than by whichever
/// consumer mod happens to have a GUI test.
/// </summary>
public static class GuiAffordanceTests
{
    /// <summary>
    /// Runs headless, on purpose: the reflection Hud depends on should break the ordinary
    /// suite when a game update renames the field, not only the occasional headful run.
    /// </summary>
    [GameTest]
    public static void HoverBoxLabelFieldStillExists()
    {
        if (!Hud.HoverBoxLabelFieldExists)
            throw new AssertionException(
                "HoveredMaterialHint no longer has the private Label field Hud.HoverBoxText " +
                "reads; the game changed shape and Hud needs updating");
    }

    /// <summary>
    /// Under a headless run this test must never execute: the gate turns it into an
    /// abstention before the body runs. Under a headful run it executes and passes. Either
    /// way a run that reaches this body with no display has caught a broken gate.
    /// </summary>
    [GameTest(RequiresDisplay = true)]
    public static void RequiresDisplayNeverRunsHeadless()
    {
        if (DisplayServer.GetName() == "headless")
            throw new AssertionException(
                "a RequiresDisplay test ran under a headless display server; the gate in " +
                "TestExecutor.Start is not holding");
    }

    /// <summary>
    /// The whole stack, end to end, on vanilla content: enter a session, look at a tile
    /// nothing has explored, put a known material there, hover it, and read the name the
    /// game shows the player. The hover box's first line is Materials.GetLocalizedName of
    /// the hovered cell, so the assertion needs nothing but the game itself.
    ///
    /// Exercises everything View.LookAt promises at once: the welcome window is held
    /// closed, the avatar-leashed camera is held on the tile, and the fog that covers an
    /// unexplored spot is lifted, since the box refuses to draw over any of those.
    /// </summary>
    [GameTest(RequiresDisplay = true)]
    public static IEnumerator HoverBoxNamesTheMaterialUnderTheCursor()
    {
        yield return Session.Enter("flat");

        if (Game.World?.Spaceship == null)
            Harness.Inapplicable("no spaceship to anchor a world position on");

        // Deliberately away from the spaceship, in sky nothing has explored, so the fog
        // lift is load-bearing rather than incidentally satisfied by the starting reveal.
        var tile = Game.World.Spaceship.GlobalPosition.GlobalToTileposI() + new Vector2I(200, 0);

        // Aim first, then place: moving the view into a region the game has not activated
        // streams its planet segment in over anything set beforehand. Once the view has
        // settled the region is live, so the pixel set here stays put (LookAt's per-frame
        // hold re-clears fog around it but leaves solid material alone).
        yield return View.LookAt(tile);
        yield return Cursor.Hover(tile);

        Session.SetPixel(tile.X, tile.Y, "Granite");
        yield return Wait.Frames(2);   // the box rebuilds its text from the tile each frame

        if (!Hud.HoverBoxVisible)
            throw new AssertionException(
                $"the hover box is not visible over a placed pixel at {tile}: " +
                $"windowOpen={Gameplay.WindowIsOpen}, overHUD={Gameplay.MouseIsOverHUD}, " +
                $"fog={Gameplay.IsFogOfWar(tile)}, mouseTile={Utils.GetTileMousePosition()}, " +
                $"materialAtTile={Simulation.CurrentState.Field.Get(tile.X, tile.Y)}");

        var text = Hud.HoverBoxText();
        var expected = Materials.GetLocalizedName(Materials.GetBaseMaterialId("Granite"));
        if (string.IsNullOrEmpty(expected))
            throw new AssertionException("the game has no localized name for Granite to compare against");
        if (!text.Contains(expected))
            throw new AssertionException(
                $"the hover box does not name the hovered material.\nexpected to contain: " +
                $"\"{expected}\"\nbox text was:\n{text}");

        yield return Session.Leave();
    }
}
