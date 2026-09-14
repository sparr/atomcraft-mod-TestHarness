using System.Collections;
using Atomcraft;
using Atomcraft.TestHarness;
using Godot;

// The game has a Session type of its own; the harness's is the one meant here.
using Session = Atomcraft.TestHarness.Session;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// The visual feedback affordances: the raised zoom limit, the cell-to-screen mapping, the
/// bitmap font, and the overlay's marks and per-pixel painters.
///
/// Split deliberately between what a headless run can judge and what it cannot. The glyph
/// table, the label metrics, the zoom limit and the game-shape probes are all arithmetic or
/// static state, so they run in the ordinary suite and catch a game update the day it lands.
/// Only the tests that need something to actually appear on a screen are gated behind
/// RequiresDisplay.
/// </summary>
public static class OverlayTests
{
    // ------------------------------------------------------------------- headless

    /// <summary>
    /// The zoom control writes FollowCam's private easing target, because setting the
    /// camera's zoom alone is undone on the next frame. Losing the field breaks both the
    /// raised limit and View.SetZoom, so it should fail the ordinary suite rather than
    /// waiting for someone to run headful.
    /// </summary>
    [GameTest]
    public static void ZoomTargetFieldStillExists()
    {
        if (!View.ZoomTargetFieldExists)
            throw new AssertionException(
                "FollowCam no longer has the private TargetZoom field View reads; the game " +
                "changed shape and View needs updating");
    }

    /// <summary>
    /// The glyph table is hand-written, and a miscounted row or column would shift every
    /// character after it in the atlas without any error. Validate is pure arithmetic, so
    /// this needs no display.
    /// </summary>
    [GameTest]
    public static void EveryGlyphIsWellFormed() => PixelFont.Validate();

    /// <summary>
    /// The advertised geometry: a 3x5 glyph on a 4x6 grid, with the spacing between
    /// characters and not after the last one, so a centered label really is centered.
    /// </summary>
    [GameTest]
    public static void LabelsMeasureToTheAdvertisedGrid()
    {
        Expect(new Vector2I(3, 5), Overlay.MeasureLabel("7"), "one Tiny character");
        Expect(new Vector2I(7, 5), Overlay.MeasureLabel("42"), "two Tiny characters");
        Expect(new Vector2I(3, 11), Overlay.MeasureLabel("4\n2"), "two Tiny lines");
        Expect(new Vector2I(14, 10), Overlay.MeasureLabel("42", TextSize.Small), "two Small characters");
        Expect(new Vector2I(28, 20), Overlay.MeasureLabel("42", TextSize.Large), "two Large characters");

        static void Expect(Vector2I want, Vector2I got, string what)
        {
            if (want != got)
                throw new AssertionException($"{what} should measure {want}, measured {got}");
        }
    }

    /// <summary>
    /// MaxZoomFactor is a limit, and a limit that silently accepts nonsense is not one. Zero
    /// and negative factors would zoom out, and past the documented ceiling there is nothing
    /// useful to see, so both are refused rather than clamped.
    /// </summary>
    [GameTest]
    public static void MaxZoomFactorRefusesWhatItCannotHonor()
    {
        var restore = View.MaxZoomFactor;
        try
        {
            Refuses(0);
            Refuses(-1);
            Refuses(View.MaxZoomFactorLimit + 1);

            for (var factor = 1; factor <= View.MaxZoomFactorLimit; factor++)
            {
                View.MaxZoomFactor = factor;
                if (Math.Abs(View.MaxZoom - View.GameMaxZoom * factor) > 0.0001f)
                    throw new AssertionException(
                        $"MaxZoomFactor {factor} should give MaxZoom {View.GameMaxZoom * factor}, " +
                        $"gave {View.MaxZoom}");
            }
        }
        finally
        {
            View.MaxZoomFactor = restore;
        }

        static void Refuses(int factor)
        {
            try
            {
                View.MaxZoomFactor = factor;
            }
            catch (AssertionException)
            {
                return;
            }
            throw new AssertionException($"MaxZoomFactor accepted {factor}, which it should refuse");
        }
    }

    /// <summary>
    /// The raised limit has to actually raise the game's own clamp, not just the harness's
    /// idea of it: a mod asking for more zoom wants the player's zoom-in key to keep going,
    /// and that key runs FollowCam.IncreaseZoom, which clamps at 1.5 unpatched.
    ///
    /// Driven by calling IncreaseZoom directly with a generous elapsed time, which is what
    /// the game's own input handler does with the real one. Needs no session and no display:
    /// the camera node exists from Game._Ready and the clamp is static arithmetic.
    /// </summary>
    [GameTest]
    public static void RaisingTheLimitRaisesTheGamesOwnClamp()
    {
        var cam = Client.FollowCam;
        if (cam == null)
            Harness.Inapplicable("no FollowCam on this build, so the zoom clamp cannot be driven");

        var restoreFactor = View.MaxZoomFactor;
        var restoreZoom = cam.Zoom;
        try
        {
            View.MaxZoomFactor = 1;
            var stock = ZoomInHard(cam);
            if (Math.Abs(stock - View.GameMaxZoom) > 0.001f)
                throw new AssertionException(
                    $"unpatched, zooming all the way in should stop at {View.GameMaxZoom}; " +
                    $"stopped at {stock}. Either the game retuned its limit or the patch is " +
                    "applying when MaxZoomFactor is 1.");

            View.MaxZoomFactor = 4;
            var raised = ZoomInHard(cam);
            if (Math.Abs(raised - View.MaxZoom) > 0.001f)
                throw new AssertionException(
                    $"with MaxZoomFactor 4, zooming all the way in should reach {View.MaxZoom}; " +
                    $"reached {raised}");
        }
        finally
        {
            // Order matters, and the limit comes first: putting it back pulls the camera
            // inside it, and only then is the zoom this test found still a legal one to ask
            // for.
            View.MaxZoomFactor = restoreFactor;
            View.SetZoom(Math.Min(restoreZoom.X, View.MaxZoom));
        }

        // Enough calls that the game's own per-call increment cannot be the thing that stops
        // it: whatever it stops at is the clamp.
        static float ZoomInHard(FollowCam cam)
        {
            for (var i = 0; i < 50; i++)
                cam.IncreaseZoom(1f);
            return View.ZoomTarget;
        }
    }

    /// <summary>
    /// Lowering the limit is a limit: the camera comes back inside it rather than sitting
    /// past a ceiling it is no longer allowed to be past.
    ///
    /// The case this guards is a test that raises the limit, zooms in, and politely puts the
    /// limit back, leaving the next test looking at a view it never asked for. That failure
    /// is silent and intermittent, which is exactly the kind worth a test of its own.
    /// </summary>
    [GameTest]
    public static void LoweringTheLimitBringsTheCameraBackInside()
    {
        if (Client.FollowCam == null)
            Harness.Inapplicable("no FollowCam on this build, so the camera cannot be moved");

        var restore = View.MaxZoomFactor;
        try
        {
            View.MaxZoomFactor = 8;
            View.SetZoom(View.GameMaxZoom * 8f);

            View.MaxZoomFactor = 1;
            if (View.Zoom > View.GameMaxZoom + 0.001f)
                throw new AssertionException(
                    $"after lowering the limit to {View.GameMaxZoom} the camera is still at " +
                    $"{View.Zoom}");
            if (View.ZoomTarget > View.GameMaxZoom + 0.001f)
                throw new AssertionException(
                    $"the camera came back to {View.Zoom} but is still easing toward " +
                    $"{View.ZoomTarget}, so it will drift straight back out");
        }
        finally
        {
            View.MaxZoomFactor = restore;
        }
    }

    /// <summary>
    /// Without a display the overlay does nothing, and says so once rather than throwing.
    /// A mod that registers a painter in its Initialize should not have to know whether this
    /// particular run has a screen.
    /// </summary>
    [GameTest]
    public static void PaintersAreInertWithoutADisplay()
    {
        if (DisplayServer.GetName() != "headless")
            Harness.Inapplicable("this run has a display, so there is nothing inert to check");

        try
        {
            Overlay.Fill(new Vector2I(1, 1), Colors.Red);
            Overlay.Outline(new Vector2I(1, 1), Colors.Red);
            Overlay.Label(new Vector2I(1, 1), "x", Colors.Red);
            Overlay.SetPainter("inert", _ => throw new Exception("a painter ran with no display"));
            if (Overlay.PixelsPaintedLastFrame != 0)
                throw new AssertionException(
                    $"a headless run painted {Overlay.PixelsPaintedLastFrame} pixels");
        }
        finally
        {
            Overlay.Reset();
        }
    }

    // -------------------------------------------------------------------- headful

    /// <summary>
    /// The cell-to-screen mapping has to agree with the game's own, or a mark lands next to
    /// the pixel it names. Checked by round-tripping through the game's inverse
    /// (Utils.ScreenPositionToWorldPosition), so a change to either side fails this.
    ///
    /// Checked across the view rather than at one point, because an error in the camera term
    /// vanishes at the center of the screen, which is exactly where a single-point test would
    /// put it.
    /// </summary>
    [GameTest(RequiresDisplay = true)]
    public static IEnumerator ScreenCoordinatesRoundTripThroughTheGamesOwnMapping()
    {
        yield return Session.Enter("flat");
        var center = Anchor();
        yield return View.LookAt(center);

        foreach (var offset in new[]
                 {
                     new Vector2I(0, 0), new Vector2I(5, 3), new Vector2I(-5, -3),
                     new Vector2I(20, -12), new Vector2I(-20, 12),
                 })
        {
            var tile = center + offset;
            var back = View.TileAt(View.ScreenOf(tile));
            if (back != tile)
                throw new AssertionException(
                    $"the center of {tile} is at {View.ScreenOf(tile)}, which the game reads " +
                    $"back as {back}. The harness and the game disagree about where a cell is.");

            var rect = View.ScreenRectOf(tile);
            if (Math.Abs(rect.Size.X - View.CellScreenSize) > 0.001f)
                throw new AssertionException(
                    $"a cell measures {rect.Size.X} across but CellScreenSize says {View.CellScreenSize}");
        }

        if (!View.IsVisible(center))
            throw new AssertionException("the tile the view is held on is not reported visible");

        yield return Session.Leave();
    }

    /// <summary>
    /// The whole point of the raised limit, end to end: a cell really does get bigger on
    /// screen, by the factor asked for. Eight times the game's own limit puts one cell at 96
    /// screen pixels, which is what makes a label inside a single pixel legible.
    /// </summary>
    [GameTest(RequiresDisplay = true)]
    public static IEnumerator ZoomingInPastTheGamesLimitEnlargesACell()
    {
        yield return Session.Enter("flat");
        var tile = Anchor();
        yield return View.LookAt(tile);

        var restore = View.MaxZoomFactor;
        try
        {
            View.MaxZoomFactor = 8;
            yield return View.SetZoom(View.GameMaxZoom * 8f);

            if (Math.Abs(View.Zoom - View.GameMaxZoom * 8f) > 0.001f)
                throw new AssertionException(
                    $"asked for zoom {View.GameMaxZoom * 8f}, camera is at {View.Zoom}");

            var expected = 8f * 8f * View.GameMaxZoom;   // 8 world units per cell, times the zoom
            if (Math.Abs(View.CellScreenSize - expected) > 0.001f)
                throw new AssertionException(
                    $"a cell should be {expected} screen pixels at this zoom, is {View.CellScreenSize}");

            // A whole Tiny label has to fit inside one cell at this zoom; that is the claim
            // the smallest font size is making.
            var label = Overlay.MeasureLabel("1234", TextSize.Tiny);
            if (label.X > View.CellScreenSize)
                throw new AssertionException(
                    $"a four character Tiny label is {label.X} pixels wide but a cell is only " +
                    $"{View.CellScreenSize}");
        }
        finally
        {
            View.MaxZoomFactor = restore;
        }

        yield return Session.Leave();
    }

    /// <summary>
    /// A painter is handed the cells that are on screen, with what is in them, and nothing
    /// else. Proven against a pixel this test places itself: the painter has to see that
    /// exact tile carrying that exact material, and the screen rectangle it is given has to
    /// be where the mapping says the cell is.
    /// </summary>
    [GameTest(RequiresDisplay = true)]
    public static IEnumerator APainterSeesEveryVisibleCell()
    {
        yield return Session.Enter("flat");
        var tile = Anchor();
        yield return View.LookAt(tile);

        Session.SetPixel(tile.X, tile.Y, "Granite");
        var granite = Materials.GetBaseMaterialId("Granite");

        var seen = 0;
        short sawMaterial = -1;
        var sawRect = new Rect2();
        Overlay.SetPainter("probe", p =>
        {
            if (p.Tile != tile)
                return;
            seen++;
            sawMaterial = p.MaterialTypeId;
            sawRect = p.Screen;
            p.Outline(Colors.Lime, 2f);
        });

        try
        {
            yield return Wait.Frames(3);

            if (seen == 0)
                throw new AssertionException(
                    $"the painter was never handed {tile}, though the view is held on it. " +
                    $"It painted {Overlay.PixelsPaintedLastFrame} cells, over {View.VisibleTiles.min} " +
                    $"to {View.VisibleTiles.max}.");
            if (sawMaterial != granite)
                throw new AssertionException(
                    $"the painter saw material {sawMaterial} at {tile}, expected Granite ({granite}). " +
                    "The painter is reading a different id space than SimField stores.");
            if (sawRect.Position.DistanceTo(View.ScreenRectOf(tile).Position) > 1f)
                throw new AssertionException(
                    $"the painter placed {tile} at {sawRect.Position} but View.ScreenRectOf says " +
                    $"{View.ScreenRectOf(tile).Position}");
            if (Overlay.PixelsPaintedLastFrame < 1)
                throw new AssertionException("no cells were reported painted");
        }
        finally
        {
            Overlay.RemovePainter("probe");
        }

        yield return Session.Leave();
    }

    /// <summary>
    /// An Alt-gated painter costs nothing on the frames nobody asked for it. Only the
    /// not-held direction is asserted: holding a real modifier means synthesizing OS input,
    /// which a test cannot do reliably, and the expensive mistake is a painter that runs when
    /// it was told not to.
    /// </summary>
    [GameTest(RequiresDisplay = true)]
    public static IEnumerator AnAltGatedPainterStaysIdleUntilAltIsHeld()
    {
        yield return Session.Enter("flat");
        var tile = Anchor();
        yield return View.LookAt(tile);

        if (Overlay.AltHeld)
            Harness.Inapplicable("something is holding Alt, so an idle gate cannot be observed");

        var ran = false;
        Overlay.SetPainter("gated", _ => ran = true, OverlayWhen.AltHeld);
        try
        {
            yield return Wait.Frames(3);
            if (ran)
                throw new AssertionException("an AltHeld painter ran with Alt not held");
            if (Overlay.PixelsPaintedLastFrame != 0)
                throw new AssertionException(
                    $"{Overlay.PixelsPaintedLastFrame} cells were walked for a painter that " +
                    "was not due to run; the gate is being checked per cell rather than per frame");

            // An ungated painter under the same conditions does run, so the idleness above is
            // the gate and not simply a painter that never runs.
            Overlay.SetPainter("ungated", _ => ran = true);
            yield return Wait.Frames(3);
            if (!ran)
                throw new AssertionException(
                    "an ungated painter did not run either, so the previous check proved nothing");
        }
        finally
        {
            Overlay.Reset();
        }

        yield return Session.Leave();
    }

    /// <summary>
    /// A mark actually reaches the framebuffer. Everything else here checks the arithmetic
    /// leading up to the draw; this reads the rendered frame back and looks at the pixel.
    ///
    /// Deliberately fills over a cell whose own colour is nothing like the fill, and samples
    /// the middle of the cell rather than its edge, so neither the material underneath nor a
    /// rounding error at a boundary can produce a pass.
    /// </summary>
    [GameTest(RequiresDisplay = true)]
    public static IEnumerator AFillReachesTheRenderedFrame()
    {
        yield return Session.Enter("flat");
        var tile = Anchor();
        yield return View.LookAt(tile);

        var restore = View.MaxZoomFactor;
        try
        {
            // Zoomed in, so the cell is a large target on screen and a pixel or two of camera
            // drift cannot move the sample off it.
            View.MaxZoomFactor = 8;
            yield return View.SetZoom(View.GameMaxZoom * 8f);

            var fill = new Color(1f, 0f, 1f);           // magenta: nothing in the world is this
            Overlay.Fill(tile, fill);
            yield return Wait.Frames(3);

            var image = Game.CanvasLayer.GetViewport().GetTexture()?.GetImage();
            if (image == null || image.GetWidth() == 0)
                Harness.Inapplicable("the viewport cannot be read back on this renderer");

            var at = View.ScreenOf(tile);
            var x = Mathf.Clamp((int)at.X, 0, image.GetWidth() - 1);
            var y = Mathf.Clamp((int)at.Y, 0, image.GetHeight() - 1);
            var got = image.GetPixel(x, y);

            if (Distance(got, fill) > 0.1f)
                throw new AssertionException(
                    $"the rendered frame shows {got} at the center of filled cell {tile} " +
                    $"(screen {x},{y}), expected {fill}. The overlay is not reaching the screen, " +
                    "or it is drawing somewhere other than where ScreenOf says.");
        }
        finally
        {
            View.MaxZoomFactor = restore;
            Overlay.Clear();
        }

        yield return Session.Leave();

        static float Distance(Color a, Color b) =>
            Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    }

    /// <summary>
    /// Glyphs come out the right way up and the right way round, and land where the metrics
    /// say they do.
    ///
    /// Nothing else here would catch an atlas built upside down or a region computed from the
    /// wrong end: a flipped font still measures correctly, still fills the same rectangle, and
    /// still looks like text at a glance. So this draws a letter whose shape is asymmetric in
    /// both axes, 'L', large enough that each font pixel is a 4x4 block of screen pixels, and
    /// samples the middle of four of those blocks in the rendered frame. Vertical flip lights
    /// the top right; horizontal flip darkens the top left.
    ///
    /// The frame is also saved as an artifact, so a headful run leaves behind a picture of the
    /// overlay that someone can simply look at.
    /// </summary>
    [GameTest(RequiresDisplay = true)]
    public static IEnumerator GlyphsDrawTheRightWayUp()
    {
        yield return Session.Enter("flat");
        var tile = Anchor();
        yield return View.LookAt(tile);

        var restore = View.MaxZoomFactor;
        try
        {
            View.MaxZoomFactor = 8;
            yield return View.SetZoom(View.GameMaxZoom * 8f);

            // Black under white, so "lit" and "unlit" are unambiguous whatever the world is
            // doing underneath, and both are painted by the overlay in the intended order.
            Overlay.Fill(tile, Colors.Black);
            Overlay.Label(tile, "L", Colors.White, TextSize.Large);
            yield return Wait.Frames(3);

            var image = Game.CanvasLayer.GetViewport().GetTexture()?.GetImage();
            if (image == null || image.GetWidth() == 0)
                Harness.Inapplicable("the viewport cannot be read back on this renderer");

            Artifacts.WriteBytes("overlay.png", image.SavePngToBuffer());

            const int scale = (int)TextSize.Large;
            var measured = Overlay.MeasureLabel("L", TextSize.Large);
            var origin = View.ScreenOf(tile) - (Vector2)measured / 2f;

            // 'L' is  #..  #..  #..  #..  ###
            Lit(0, 0, "the top of the stem");
            Dark(2, 0, "the top right, which is only lit if the glyph is upside down");
            Lit(0, 4, "the foot of the stem");
            Lit(2, 4, "the end of the foot");
            Dark(2, 2, "the open middle right");

            void Lit(int fx, int fy, string what)
            {
                var c = Sample(fx, fy);
                if (c.R + c.G + c.B < 2.5f)
                    throw new AssertionException(
                        $"{what} of a Large 'L' should be white, the frame shows {c}");
            }

            void Dark(int fx, int fy, string what)
            {
                var c = Sample(fx, fy);
                if (c.R + c.G + c.B > 0.5f)
                    throw new AssertionException(
                        $"{what} of a Large 'L' should be unlit black, the frame shows {c}");
            }

            // The middle of the font pixel's block, so being a pixel out anywhere upstream
            // still reads the block that was meant.
            Color Sample(int fx, int fy)
            {
                var x = (int)Math.Round(origin.X) + fx * scale + scale / 2;
                var y = (int)Math.Round(origin.Y) + fy * scale + scale / 2;
                return image.GetPixel(Mathf.Clamp(x, 0, image.GetWidth() - 1),
                                      Mathf.Clamp(y, 0, image.GetHeight() - 1));
            }
        }
        finally
        {
            View.MaxZoomFactor = restore;
            Overlay.Clear();
        }

        yield return Session.Leave();
    }

    /// <summary>
    /// A tile in open sky well away from the spaceship: far enough that the fog lift and the
    /// view hold are doing real work, and empty enough that holding the avatar there does not
    /// fight the game's unstick logic.
    /// </summary>
    private static Vector2I Anchor()
    {
        if (Game.World?.Spaceship == null)
            Harness.Inapplicable("no spaceship to anchor a world position on");
        return Game.World.Spaceship.GlobalPosition.GlobalToTileposI() + new Vector2I(200, 0);
    }
}
