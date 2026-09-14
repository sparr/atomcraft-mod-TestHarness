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
    public static void EveryGlyphIsWellFormed() => PixelFont.ValidateAll();

    /// <summary>
    /// The advertised geometry: a 3x5 glyph on a 4x6 grid, with the spacing between
    /// characters and not after the last one, so a centered label really is centered.
    /// </summary>
    [GameTest]
    public static void LabelsMeasureToTheAdvertisedGrid()
    {
        // Small: 3x5 glyphs, 1 spacing. A lone glyph measures its lit area; a second costs a
        // full advance; a second line costs a line height. The other sizes are the same
        // arithmetic with their own numbers, which is the whole contract of a bitmap font.
        Expect(new Vector2I(3, 5), Overlay.MeasureLabel("7"), "one Small character");
        Expect(new Vector2I(7, 5), Overlay.MeasureLabel("42"), "two Small characters");
        Expect(new Vector2I(3, 11), Overlay.MeasureLabel("4\n2"), "two Small lines");

        // Medium: 5x7 glyphs, 2 spacing.
        Expect(new Vector2I(5, 7), Overlay.MeasureLabel("7", TextSize.Medium), "one Medium character");
        Expect(new Vector2I(12, 7), Overlay.MeasureLabel("42", TextSize.Medium), "two Medium characters");
        Expect(new Vector2I(5, 16), Overlay.MeasureLabel("4\n2", TextSize.Medium), "two Medium lines");

        // Large: 9x13 glyphs, 3 spacing.
        Expect(new Vector2I(9, 13), Overlay.MeasureLabel("7", TextSize.Large), "one Large character");
        Expect(new Vector2I(21, 13), Overlay.MeasureLabel("42", TextSize.Large), "two Large characters");
        Expect(new Vector2I(9, 29), Overlay.MeasureLabel("4\n2", TextSize.Large), "two Large lines");

        // Scale multiplies whichever size was named.
        Expect(new Vector2I(42, 26), Overlay.MeasureLabel("42", TextSize.Large, scale: 2),
               "two Large characters at 2x");

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

            // A whole Small label has to fit inside one cell at this zoom; that is the claim
            // the smallest font is making.
            var label = Overlay.MeasureLabel("1234", TextSize.Small);
            if (label.X > View.CellScreenSize)
                throw new AssertionException(
                    $"a four character Small label is {label.X} pixels wide but a cell is only " +
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
        // Blocks bigger than one pixel, so sampling their middles has something to be
        // tolerant with; at scale 1 a 'block' is a single pixel and the tolerance is gone.
        const int LabelScale = 3;

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
            Overlay.Label(tile, "L", Colors.White, TextSize.Large, scale: LabelScale);
            yield return Wait.Frames(3);

            var image = Game.CanvasLayer.GetViewport().GetTexture()?.GetImage();
            if (image == null || image.GetWidth() == 0)
                Harness.Inapplicable("the viewport cannot be read back on this renderer");

            Artifacts.WriteBytes("overlay.png", image.SavePngToBuffer());

            var measured = Overlay.MeasureLabel("L", TextSize.Large, LabelScale);
            var origin = View.ScreenOf(tile) - (Vector2)measured / 2f;

            // Large 'L' is a two-pixel stem down the left with the foot on the baseline,
            // row 9 of the 13: "##......." nine times over, then "#########".
            Lit(0, 0, "the top of the stem");
            Dark(4, 0, "the top right, which is only lit if the glyph is upside down");
            Lit(0, 9, "the foot of the stem");
            Lit(8, 9, "the far end of the foot");
            Dark(4, 4, "the open middle right");
            Dark(4, 11, "the descender zone, which an 'L' does not reach into");

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
                var x = (int)Math.Round(origin.X) + fx * LabelScale + LabelScale / 2;
                var y = (int)Math.Round(origin.Y) + fy * LabelScale + LabelScale / 2;
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
    /// Every font pixel arrives as a block of exactly the size it was drawn at.
    ///
    /// GlyphsDrawTheRightWayUp samples the middle of each block on purpose, so that being a
    /// pixel out anywhere upstream still reads the block that was meant. That tolerance is
    /// right for checking a glyph's shape and it is precisely why it cannot see a doubled row:
    /// a glyph stretched by a fraction still has the right pixel in the middle of every block.
    /// This measures the extent instead. '|' is one lit column five rows tall, so at scale N it
    /// has to arrive as a solid block exactly N by 5N; anything else is a row or column the
    /// frame gained or lost between the draw and the render target.
    ///
    /// A filled patch underneath bounds the search, so nothing here depends on what the world
    /// happens to look like.
    ///
    /// What is read back is the game's render target, which is what the overlay draws into.
    /// The engine rescales that finished frame to the window afterwards, and whether that
    /// rescale preserves a one-pixel row is a display concern outside this test and outside
    /// the harness; see Overlay.WindowScale and Overlay.PixelPerfect. This test is the claim
    /// the harness can actually make: what it drew was exact when it left.
    /// </summary>
    [GameTest(RequiresDisplay = true)]
    public static IEnumerator GlyphsArriveInTheFrameAsExactBlocks()
    {
        yield return Session.Enter("flat");
        var tile = Anchor();
        yield return View.LookAt(tile);

        var ink = new Color(1f, 0f, 1f);
        const int scale = 4;

        var restore = View.MaxZoomFactor;
        try
        {
            // At maximum zoom, which is where a doubled row is noticed, and where the glyph
            // has the most cell to sit in. The drawing is zoom independent, so this is about
            // reproducing the conditions someone reports from rather than about the geometry.
            View.MaxZoomFactor = 8;
            yield return View.SetZoom(View.MaxZoom);

            // Opaque and larger than the glyph, so every pixel searched is either backdrop or
            // ink and the world underneath cannot be mistaken for either.
            var patch = new RectInt(tile.X - 4, tile.Y - 4, 9, 9);
            Overlay.Fill(patch, Colors.Black);
            // Small on purpose: its '|' is exactly one lit column five rows tall, which makes
            // the expected block trivially stateable. DrawLabel is shared by all three sizes,
            // so proving the path is exact for one proves it for all.
            Overlay.Label(tile, "|", ink, TextSize.Small, scale: scale);
            yield return Wait.Frames(3);

            var image = Game.CanvasLayer.GetViewport().GetTexture()?.GetImage();
            if (image == null || image.GetWidth() == 0)
                Harness.Inapplicable("the viewport cannot be read back on this renderer");

            var area = View.ScreenRectOf(new Vector2I(patch.X, patch.Y));
            var cell = View.CellScreenSize;
            var x0 = Math.Max(0, Mathf.FloorToInt(area.Position.X));
            var y0 = Math.Max(0, Mathf.FloorToInt(area.Position.Y));
            var x1 = Math.Min(image.GetWidth(), Mathf.CeilToInt(area.Position.X + patch.width * cell));
            var y1 = Math.Min(image.GetHeight(), Mathf.CeilToInt(area.Position.Y + patch.height * cell));

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, lit = 0;
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var c = image.GetPixel(x, y);
                if (c.R <= 0.5f || c.G >= 0.5f || c.B <= 0.5f)
                    continue;
                lit++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

            if (lit == 0)
                throw new AssertionException(
                    $"no glyph reached the frame inside the patch at ({x0},{y0})-({x1},{y1}); " +
                    $"zoom={View.Zoom}, cell={cell}, windowScale={Overlay.WindowScale}");

            var width = maxX - minX + 1;
            var height = maxY - minY + 1;
            var wantHeight = PixelFont.Small.GlyphHeight * scale;
            if (width != scale || height != wantHeight || lit != width * height)
                throw new AssertionException(
                    $"a Small '|' at {scale}x arrived as a {width}x{height} shape covering {lit} pixels; " +
                    $"expected a solid {scale}x{wantHeight} block. A bar wider or taller than " +
                    "that is a doubled column or row, which means the frame was resampled " +
                    $"between the draw and the read. windowScale={Overlay.WindowScale}, " +
                    $"pixelPerfect={Overlay.PixelPerfect}");
        }
        finally
        {
            View.MaxZoomFactor = restore;
            Overlay.Clear();
        }

        yield return Session.Leave();
    }


    /// <summary>
    /// Auto picks the largest size that fits, and falls back rather than drawing nothing.
    ///
    /// Pure arithmetic over the three sizes' metrics, so it runs headless: what a cell of a
    /// given size can hold does not depend on there being a screen.
    /// </summary>
    [GameTest]
    public static void AutoPicksTheLargestSizeThatFits()
    {
        // "42" needs 21x13 in Large, 12x7 in Medium, 7x5 in Small.
        Expect(new Vector2(96, 96), PixelFont.Large, "a cell with room for anything");
        Expect(new Vector2(21, 13), PixelFont.Large, "a cell that fits Large exactly");
        Expect(new Vector2(20, 13), PixelFont.Medium, "a cell one pixel too narrow for Large");
        Expect(new Vector2(21, 12), PixelFont.Medium, "a cell one pixel too short for Large");
        Expect(new Vector2(12, 7), PixelFont.Medium, "a cell that fits Medium exactly");
        Expect(new Vector2(11, 7), PixelFont.Small, "a cell one pixel too narrow for Medium");
        Expect(new Vector2(7, 5), PixelFont.Small, "a cell that fits Small exactly");

        // Nothing fits, and the smallest is drawn anyway: a label spilling past its cell can
        // still be read, and an empty cell is indistinguishable from one nobody labelled.
        Expect(new Vector2(2, 2), PixelFont.Small, "a cell too small for any size");

        // Scale is part of what has to fit, so raising it steps down through the sizes rather
        // than overflowing.
        if (PixelFont.LargestFitting("42", new Vector2(21, 13), scale: 2) != PixelFont.Small)
            throw new AssertionException(
                "at 2x, \"42\" needs 42x26 in Large and 24x14 in Medium, so a 21x13 cell should " +
                $"fall to Small; got {PixelFont.LargestFitting("42", new Vector2(21, 13), 2)}");

        static void Expect(Vector2 box, PixelFont want, string what)
        {
            var got = PixelFont.LargestFitting("42", box);
            if (got != want)
                throw new AssertionException($"{what} ({box}) chose {got}, expected {want}");
        }
    }

    /// <summary>
    /// The sizes are distinct fonts rather than one font scaled, which is the whole reason for
    /// having three. Checked on the metrics, since a 3x5 glyph magnified is still 3x5.
    /// </summary>
    [GameTest]
    public static void TheThreeSizesAreDistinctFonts()
    {
        Expect(PixelFont.Small, 3, 5, 1, descenders: false);
        Expect(PixelFont.Medium, 5, 7, 2, descenders: false);
        Expect(PixelFont.Large, 9, 13, 3, descenders: true);

        static void Expect(PixelFont font, int w, int h, int spacing, bool descenders)
        {
            if (font.GlyphWidth != w || font.GlyphHeight != h || font.Spacing != spacing)
                throw new AssertionException(
                    $"{font.Name} is {font.GlyphWidth}x{font.GlyphHeight} with {font.Spacing} " +
                    $"spacing, expected {w}x{h} with {spacing}");
            if (font.HasDescenders != descenders)
                throw new AssertionException(
                    $"{font.Name} reports HasDescenders={font.HasDescenders}, expected {descenders}");
        }
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
