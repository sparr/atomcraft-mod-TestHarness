using Atomcraft;
using Godot;
using HarmonyLib;

namespace Atomcraft.TestHarness;

/// <summary>When a painter runs.</summary>
public enum OverlayWhen
{
    /// <summary>Every frame the world is on screen.</summary>
    Always,

    /// <summary>
    /// Only while the player holds Alt.
    ///
    /// Alt is already the game's own "tell me more" modifier: holding it adds mass, specific
    /// heat, laser absorption and tile coordinates to the hover box, and lights up the cells
    /// the simulation loader is working on. A painter gated this way joins that gesture
    /// instead of inventing a new one, and costs nothing on the frames nobody asked.
    /// </summary>
    AltHeld,
}

/// <summary>
/// Which bitmap font overlay text is drawn in. See <see cref="PixelFont"/> for the metrics.
/// </summary>
public enum TextSize
{
    /// <summary>
    /// The largest size that fits the cell, which is the default.
    ///
    /// A cell is <c>8 * zoom</c> screen pixels, so what fits changes as the view zooms and
    /// with how much text there is. Picking per label keeps a number inside its own pixel
    /// without the caller tracking the zoom. When even the smallest overflows, the smallest is
    /// drawn anyway: a label spilling past its cell still reads, and an empty cell does not.
    /// </summary>
    Auto,

    /// <summary>3x5 glyphs on a 4x6 grid.</summary>
    Small,

    /// <summary>5x7 glyphs on a 7x9 grid.</summary>
    Medium,

    /// <summary>9x13 glyphs on a 12x16 grid, with true descenders.</summary>
    Large,
}

/// <summary>Where a label sits relative to the cell it belongs to.</summary>
public enum LabelPlacement
{
    /// <summary>Centered on the cell. What you want when the label fits inside it.</summary>
    Center,

    /// <summary>Just inside the cell's top left corner.</summary>
    TopLeft,

    /// <summary>Centered horizontally, sitting just above the cell.</summary>
    Above,

    /// <summary>Centered horizontally, sitting just below the cell.</summary>
    Below,
}

/// <summary>
/// One cell of the world, on screen, handed to a painter.
///
/// The tile and its material are already in hand because the pass that found the cell had to
/// read them anyway, so a painter that only cares about one material can reject the rest
/// without touching the field again.
/// </summary>
public readonly struct VisiblePixel
{
    internal VisiblePixel(Vector2I tile, short materialTypeId, Rect2 screen)
    {
        Tile = tile;
        MaterialTypeId = materialTypeId;
        Screen = screen;
    }

    /// <summary>The cell's position in the simulation field.</summary>
    public Vector2I Tile { get; }

    /// <summary>
    /// What is in the cell, as a base material id (the id space <c>SimField</c> stores and
    /// <c>string.ToMaterialTypeId()</c> returns, not the one <c>Materials.GetMaterialTypeId</c>
    /// does). -1 is air.
    /// </summary>
    public short MaterialTypeId { get; }

    /// <summary>The rectangle this cell covers on screen, in viewport pixels.</summary>
    public Rect2 Screen { get; }

    /// <summary>Paints the whole cell. See <see cref="Overlay.Fill(Vector2I, Color)"/>.</summary>
    public void Fill(Color color) => Overlay.DrawFill(Screen, color);

    /// <summary>Outlines the cell. See <see cref="Overlay.Outline(Vector2I, Color, float)"/>.</summary>
    public void Outline(Color color, float thickness = 1f) => Overlay.DrawOutline(Screen, color, thickness);

    /// <summary>Writes on the cell. See <see cref="Overlay.Label"/>.</summary>
    public void Label(string text, Color color,
                      TextSize size = TextSize.Auto,
                      LabelPlacement placement = LabelPlacement.Center,
                      int scale = 1) =>
        Overlay.DrawLabel(Screen, text, color, size, placement, scale);
}

/// <summary>
/// Drawing on top of the running game, for headful tests that take screenshots and for
/// looking at a mod's own state while playing it.
///
/// Everything here is drawn in screen space, on a layer above the world and above the HUD,
/// and rebuilt from scratch every frame from cell coordinates. Two consequences are worth
/// knowing up front. Marks track their cells: pan or zoom and they stay on the pixels they
/// name. And marks are not lit, shadowed, or fogged the way the world is, so a fill is the
/// colour you asked for even over an unexplored or pitch-dark cell, which is the point when
/// what you are debugging is why a cell is not what you expected.
///
/// The alternative, writing into the texture the game builds the world from, was rejected:
/// it would put marks under the lighting and fog they most need to survive, and it would
/// bind the harness to three private fields of the game's renderer.
///
/// Two ways to use it. Retained marks stay until <see cref="Clear"/> or the end of the test:
///
///     Overlay.Outline(tile, Colors.Red, thickness: 2f);
///     Overlay.Label(tile, "leak", Colors.Red, TextSize.Small, LabelPlacement.Above);
///
/// A painter runs for every cell on screen, every frame, which is how a mod shows its own
/// per-cell state while someone plays:
///
///     Overlay.SetPainter("pressure", p =>
///     {
///         var excess = Pressure.ExcessAt(p.Tile);
///         if (excess > 0)
///             p.Label(excess.ToString(), Colors.White);
///     }, OverlayWhen.AltHeld);
///
/// Painters cost a delegate call per visible cell per frame: around 57,000 of them at the
/// zoomed-out default on a 1920x1080 window, around 900 at 8x zoom. That is affordable for
/// cheap work and is the reason <see cref="OverlayWhen.AltHeld"/> exists.
/// </summary>
public static class Overlay
{
    // ------------------------------------------------------------ retained marks

    private abstract class Mark
    {
        public abstract void Draw();
    }

    private sealed class FillMark : Mark
    {
        public RectInt Tiles; public Color Color;
        public override void Draw() => DrawFill(ScreenRectOf(Tiles), Color);
    }

    private sealed class OutlineMark : Mark
    {
        public RectInt Tiles; public Color Color; public float Thickness;
        public override void Draw() => DrawOutline(ScreenRectOf(Tiles), Color, Thickness);
    }

    private sealed class LabelMark : Mark
    {
        public Vector2I Tile; public string Text = ""; public Color Color;
        public TextSize Size; public LabelPlacement Placement; public int Scale;
        public override void Draw() =>
            DrawLabel(View.ScreenRectOf(Tile), Text, Color, Size, Placement, Scale);
    }

    private static readonly List<Mark> Marks = new();

    /// <summary>
    /// Past this many retained marks, say so once. Marks are retained on purpose, so adding
    /// one per frame is not an error, but it is nearly always someone reaching for a mark
    /// where they wanted a painter, and the symptom otherwise is a run that slows down for
    /// no visible reason.
    /// </summary>
    private const int MarkWarningThreshold = 10_000;

    private static bool _warnedManyMarks;

    private static void Add(Mark mark)
    {
        Marks.Add(mark);
        if (Marks.Count < MarkWarningThreshold || _warnedManyMarks)
            return;
        _warnedManyMarks = true;
        Log.Warn($"the overlay is holding {Marks.Count} marks, and every one is redrawn every " +
                 "frame. Marks are retained until Overlay.Clear or the end of the test; if you " +
                 "are adding them every frame, Overlay.SetPainter is the per-frame version.");
    }

    /// <summary>
    /// Paints a cell a flat colour, over whatever the game drew there.
    ///
    /// The mark is remembered against the cell, not against a place on screen, and redrawn
    /// every frame until <see cref="Clear"/> or the end of the test. Colours with alpha tint
    /// rather than replace, which is usually what you want when the material underneath is
    /// still worth seeing.
    /// </summary>
    public static void Fill(Vector2I tile, Color color) => Fill(new RectInt(tile.X, tile.Y, 1, 1), color);

    /// <summary>Paints a rectangle of cells. One mark, so a large area costs one draw rather than thousands.</summary>
    public static void Fill(RectInt tiles, Color color) => Add(new FillMark { Tiles = tiles, Color = color });

    /// <summary>
    /// Draws a coloured border around a cell.
    ///
    /// The border is drawn just inside the cell's own edges, so outlining two neighbours
    /// leaves two distinct boxes rather than one shared smear, and an outline never covers a
    /// cell it does not name. Thickness is in screen pixels, so it stays equally visible as
    /// the zoom changes rather than vanishing when you zoom out.
    /// </summary>
    public static void Outline(Vector2I tile, Color color, float thickness = 1f) =>
        Outline(new RectInt(tile.X, tile.Y, 1, 1), color, thickness);

    /// <summary>Draws one border around a whole rectangle of cells, not around each cell in it.</summary>
    public static void Outline(RectInt tiles, Color color, float thickness = 1f) =>
        Add(new OutlineMark { Tiles = tiles, Color = color, Thickness = thickness });

    /// <summary>
    /// Writes text on a cell, in a bitmap font drawn at a whole-number scale from a
    /// whole-number screen position, so every font pixel is an exact block of screen pixels
    /// at any zoom. Embedded newlines start a new line.
    ///
    /// By default the largest of the three sizes that fits the cell is used, which is usually
    /// what you want: a cell is <c>8 * zoom</c> screen pixels, so the size that reads best
    /// changes as the view zooms and with how much text there is. Name a
    /// <see cref="TextSize"/> to fix it instead, when a row of labels has to come out at one
    /// size regardless of what each one says.
    ///
    /// <paramref name="scale"/> multiplies whichever size is used, and the auto choice is made
    /// against the scaled size, so raising it picks a smaller font rather than overflowing.
    /// The sizes cover the ordinary range on their own; reach for scale when a screenshot has
    /// to be read at a glance.
    ///
    /// The size is chosen when the mark is drawn, not when it is added, so a retained label
    /// keeps choosing correctly as the view zooms.
    /// </summary>
    public static void Label(Vector2I tile, string text, Color color,
                             TextSize size = TextSize.Auto,
                             LabelPlacement placement = LabelPlacement.Center,
                             int scale = 1) =>
        Add(new LabelMark
        {
            Tile = tile, Text = text, Color = color,
            Size = size, Placement = placement, Scale = RequireScale(scale),
        });

    /// <summary>
    /// How much screen space a label would take, so a caller can place one itself rather than
    /// guess. Honors embedded newlines, and counts only the glyphs: the spacing between
    /// characters is not added after the last one.
    ///
    /// <see cref="TextSize.Auto"/> has no answer here, because what fits depends on the cell;
    /// ask <see cref="FontFor"/> with the cell you mean, or name a size.
    /// </summary>
    public static Vector2I MeasureLabel(string text, TextSize size = TextSize.Small, int scale = 1) =>
        Font(size, "MeasureLabel").Measure(text) * RequireScale(scale);

    /// <summary>
    /// Which font a label would be drawn in on a given cell: the resolution of
    /// <see cref="TextSize.Auto"/>, exposed so a caller can measure or align against the same
    /// answer the drawing will use.
    /// </summary>
    public static PixelFont FontFor(Vector2I tile, string text, TextSize size = TextSize.Auto,
                                    int scale = 1) =>
        Resolve(size, text, View.ScreenRectOf(tile).Size, RequireScale(scale));

    private static PixelFont Resolve(TextSize size, string text, Vector2 cell, int scale) =>
        size == TextSize.Auto
            ? PixelFont.LargestFitting(text, cell, scale)
            : Font(size, "Label");

    private static PixelFont Font(TextSize size, string what) => size switch
    {
        TextSize.Small  => PixelFont.Small,
        TextSize.Medium => PixelFont.Medium,
        TextSize.Large  => PixelFont.Large,
        TextSize.Auto   => throw new AssertionException(
            $"{what} needs a named TextSize; Auto means \"the largest that fits this cell\", " +
            "which has no answer without a cell. Use Overlay.FontFor(tile, text) to resolve it."),
        _ => throw new AssertionException($"unknown TextSize {size}"),
    };

    private static int RequireScale(int scale) =>
        scale >= 1 ? scale : throw new AssertionException($"a text scale must be at least 1; got {scale}");

    /// <summary>Drops every retained mark. Painters are left alone; see <see cref="RemovePainter"/>.</summary>
    public static void Clear() => Marks.Clear();

    /// <summary>
    /// Drops every mark and every painter, whoever registered them.
    ///
    /// Run automatically when a test ends, and the sweep is deliberately total: a mod's own
    /// debug painter drawing across every test's screenshot would make those screenshots
    /// evidence of something other than the test. A painter a mod registers from its
    /// Initialize therefore survives an ordinary play session, where no test ever ends, but
    /// does not survive the first test of a run.
    /// </summary>
    public static void Reset()
    {
        Marks.Clear();
        Painters.Clear();
        FrameHolds.Remove(PainterHoldName);
        _failure = null;
        // Not a Redraw: Reset is what the redraw backstop calls when a redraw threw, and
        // redrawing from there is how one bad frame becomes a loop. The canvas is emptied
        // directly, and the next frame draws whatever is registered by then.
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            RenderingServer.CanvasItemClear(_item);
    }

    // ------------------------------------------------------------------ painters

    private sealed class Painter
    {
        public string Name = "";
        public Action<VisiblePixel> Action = _ => { };
        public OverlayWhen When;
    }

    private const string PainterHoldName = "Overlay.painter";
    private static readonly List<Painter> Painters = new();
    private static readonly List<Painter> Due = new();
    private static Exception? _failure;

    /// <summary>
    /// Registers a callback to run for every cell on screen, every frame, replacing any
    /// painter already registered under the same name.
    ///
    /// The callback is handed each visible cell in turn and draws on it or does not. Draw
    /// calls made from a painter last for that frame only, which is what makes a painter the
    /// right shape for state that changes as the simulation runs.
    ///
    /// Pass <see cref="OverlayWhen.AltHeld"/> to have it run only while the player holds Alt,
    /// joining the game's own modifier for extra detail. On every other frame the painter is
    /// not called at all, so an expensive one costs nothing until someone asks for it.
    ///
    /// A painter that throws is removed rather than left to throw again next frame: Godot
    /// logs an unhandled per-frame exception every frame with no backpressure, and one bad
    /// hook is enough to fill a disk. The exception is logged, and if a test is running it
    /// fails that test on the next frame rather than disappearing into the log.
    /// </summary>
    public static void SetPainter(string name, Action<VisiblePixel> painter,
                                  OverlayWhen when = OverlayWhen.Always)
    {
        RemovePainter(name);
        Painters.Add(new Painter { Name = name, Action = painter, When = when });

        // A hold runs on every frame of a running test and fails it if it throws, which is
        // exactly how a painter's failure should reach a test. Outside a test nothing runs
        // holds, and the painter's own catch has already logged and removed it.
        FrameHolds.Set(PainterHoldName, ThrowIfPainterFailed);
    }

    /// <summary>Removes a painter by name. Removing one that was never registered is not an error.</summary>
    public static void RemovePainter(string name) => Painters.RemoveAll(p => p.Name == name);

    /// <summary>Whether the player is holding Alt right now, read the same way the game reads it.</summary>
    public static bool AltHeld => Input.IsKeyPressed(Key.Alt);

    // ------------------------------------------------------- the stretched viewport

    /// <summary>
    /// How many window pixels the engine paints for each viewport pixel, once the whole
    /// rendered frame has been scaled to the window.
    ///
    /// The project sets <c>display/window/stretch/mode = viewport</c> over a fixed 1600x900
    /// render target, so the game does not draw at the window's resolution at all: it draws
    /// at 1600x900 and the engine rescales the finished frame to whatever size the window is.
    /// Every coordinate in this class, and in <see cref="View.ScreenOf"/>, is a pixel of that
    /// render target, because that is the space the game itself computes in.
    ///
    /// The default window is 1280x720, so this is normally 0.8: the final blit resamples the
    /// entire frame, the game's own art included, and a one-pixel-wide feature survives it
    /// only by luck. That is invisible in the world art and glaring in a bitmap font, which
    /// is why <see cref="PixelPerfect"/> exists: the overlay draws exactly either way, and
    /// this says whether that exactness reaches the window.
    /// </summary>
    public static Vector2 WindowScale
    {
        get
        {
            var viewport = Game.CanvasLayer?.GetViewport().GetVisibleRect().Size ?? Vector2.Zero;
            if (viewport.X <= 0f || viewport.Y <= 0f)
                return Vector2.One;
            return (Vector2)DisplayServer.WindowGetSize() / viewport;
        }
    }

    /// <summary>
    /// Whether what is drawn here survives to the window unresampled: true when the window is
    /// the render target's size or a whole multiple of it.
    ///
    /// False does not mean the overlay is wrong, and nothing here can make it true: the
    /// rescale happens to the finished frame, after everything inside it has been drawn. It
    /// means the engine is scaling the frame by a fraction, so some rows and columns of every
    /// one-pixel feature are doubled or dropped on the way to the window and a bitmap glyph
    /// comes out lopsided. The remedy is a window the frame does not need rescaling to fill,
    /// which is a display concern rather than a drawing one.
    /// </summary>
    public static bool PixelPerfect
    {
        get
        {
            var s = WindowScale;
            return Mathf.IsEqualApprox(s.X, s.Y)
                && s.X >= 1f
                && Mathf.IsEqualApprox(s.X, Mathf.Round(s.X));
        }
    }

    /// <summary>
    /// How many cells the painters were run over on the last frame they ran. Zero when no
    /// painter is registered, or when every one of them is waiting on Alt.
    /// </summary>
    public static int PixelsPaintedLastFrame { get; private set; }

    /// <summary>Rethrows whatever a painter threw, if one did, and forgets it.</summary>
    private static void ThrowIfPainterFailed()
    {
        if (_failure == null)
            return;
        var failure = _failure;
        _failure = null;
        throw new AssertionException($"an overlay painter threw: {failure.Message}", failure);
    }

    // -------------------------------------------------------------------- drawing

    private static CanvasLayer? _layer;
    private static Rid _item;
    private static bool? _headless;
    private static bool _warnedHeadless;

    /// <summary>
    /// Above the world and above the HUD. A debug mark that the thing being debugged can
    /// cover is not much of a debug mark, and 128 is as high as Godot's canvas layers go.
    /// </summary>
    private const int LayerAboveEverything = 128;

    private static bool EnsureCanvas()
    {
        // Cached: asking the engine allocates a string, and this runs every frame.
        _headless ??= DisplayServer.GetName() == "headless";
        if (_headless.Value)
        {
            if (!_warnedHeadless)
            {
                _warnedHeadless = true;
                Log.Info("Overlay is idle: there is no display to draw on. " +
                         "Mark a test [GameTest(RequiresDisplay = true)] and run with --headful.");
            }
            return false;
        }

        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            return true;

        var root = Game.Instance?.GetTree()?.Root;
        if (root == null)
            return false;

        // A plain CanvasLayer plus a RenderingServer canvas item, rather than a Control with
        // a _Draw override: the harness is compiled without Godot's source generators, so a
        // CanvasItem subclass of ours would never have its _Draw called. The server API needs
        // no generated bridge and gives exact control over the command list.
        _layer = new CanvasLayer { Name = "AtomTestOverlay", Layer = LayerAboveEverything };
        // A canvas item is a server resource, not a node, so nothing frees it when the tree
        // goes away; without this the engine reports a leaked RID at every exit.
        _layer.TreeExiting += ReleaseCanvasItem;
        root.AddChild(_layer);

        _item = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(_item, _layer.GetCanvas());
        // Nearest, so a glyph scaled by a whole number stays a grid of hard-edged blocks.
        RenderingServer.CanvasItemSetDefaultTextureFilter(
            _item, RenderingServer.CanvasItemTextureFilter.Nearest);
        return true;
    }

    private static void ReleaseCanvasItem()
    {
        if (_item.IsValid)
            RenderingServer.FreeRid(_item);
        _item = default;
        _layer = null;
        PixelFont.ReleaseAll();
    }

    /// <summary>
    /// Rebuilds the whole overlay for this frame: retained marks first, then each painter
    /// over each visible cell. Called from a postfix on the game's own per-frame render
    /// update, so the camera it projects through is the one the world was just drawn with.
    /// </summary>
    internal static void Redraw()
    {
        if (!EnsureCanvas())
            return;

        RenderingServer.CanvasItemClear(_item);
        PixelsPaintedLastFrame = 0;

        if (!Game.InSession || UI.CurrentPageId != PageId.Gameplay)
            return;
        if (Marks.Count == 0 && Painters.Count == 0)
            return;

        // Everything below projects through the camera, and the painter pass asks the game
        // which cells it is rendering, which reads the avatar's tile. Both are missing for a
        // few frames around a world load, and skipping those frames is better than letting
        // the redraw backstop tear the overlay down over them.
        if (Client.FollowCam == null || !GodotObject.IsInstanceValid(Avatars.LocalAvatar))
            return;

        foreach (var mark in Marks)
            mark.Draw();

        RunPainters();
    }

    private static void RunPainters()
    {
        // Reused rather than reallocated: this runs every frame, and a render path that
        // allocates per frame is a render path that stutters on collection.
        Due.Clear();
        var alt = AltHeld;
        foreach (var painter in Painters)
            if (painter.When == OverlayWhen.Always || alt)
                Due.Add(painter);
        if (Due.Count == 0)
            return;

        var field = Simulation.CurrentState?.Field;
        if (field == null)
            return;

        var visible = View.VisibleTiles;
        var cell = View.CellScreenSize;

        // Projected once for the corner and stepped from there, rather than through
        // View.WorldToScreen per cell: that asks the engine for the camera and the viewport
        // on every call, and this loop runs once per visible cell per frame, which is tens of
        // thousands of times at the zoomed-out default.
        var corner = View.WorldToScreen(
            new Vector2(visible.min.X * View.TileSize, visible.min.Y * View.TileSize));

        for (var y = visible.min.Y; y < visible.max.Y; y++)
        for (var x = visible.min.X; x < visible.max.X; x++)
        {
            var tile = new Vector2I(x, y);
            var screen = new Rect2(corner.X + (x - visible.min.X) * cell,
                                   corner.Y + (y - visible.min.Y) * cell,
                                   cell, cell);
            var pixel = new VisiblePixel(tile, field.Get(x, y), screen);

            foreach (var painter in Due)
            {
                try
                {
                    painter.Action(pixel);
                }
                catch (Exception ex)
                {
                    // Removed rather than retried: an unhandled per-frame exception is logged
                    // by Godot every frame with no backpressure, and a painter that threw on
                    // one cell will throw on the next one too.
                    Painters.Remove(painter);
                    _failure ??= ex;
                    Log.Error($"overlay painter '{painter.Name}' threw at {tile} and has been " +
                              $"removed: {ex}");
                    return;
                }
            }
            PixelsPaintedLastFrame++;
        }
    }

    private static Rect2 ScreenRectOf(RectInt tiles)
    {
        var topLeft = View.WorldToScreen(new Vector2(tiles.min.X * View.TileSize, tiles.min.Y * View.TileSize));
        var cell = View.CellScreenSize;
        return new Rect2(topLeft, new Vector2(tiles.width * cell, tiles.height * cell));
    }

    internal static void DrawFill(Rect2 screen, Color color) =>
        RenderingServer.CanvasItemAddRect(_item, Snap(screen), color);

    internal static void DrawOutline(Rect2 screen, Color color, float thickness)
    {
        var r = Snap(screen);
        // At least one pixel, and never more than half the box: past that the two side bars
        // would be asked for a negative height, and a thick outline on a small cell is a
        // filled cell anyway.
        var t = Mathf.Clamp(Mathf.Round(thickness), 1f, Mathf.Floor(Math.Min(r.Size.X, r.Size.Y) / 2f));

        // Four rects rather than a polyline: a stroked line straddles its path, so half of it
        // would land on the neighbouring cell. These sit wholly inside the cell.
        RenderingServer.CanvasItemAddRect(_item, new Rect2(r.Position.X, r.Position.Y, r.Size.X, t), color);
        RenderingServer.CanvasItemAddRect(_item, new Rect2(r.Position.X, r.End.Y - t, r.Size.X, t), color);
        RenderingServer.CanvasItemAddRect(_item, new Rect2(r.Position.X, r.Position.Y + t, t, r.Size.Y - 2 * t), color);
        RenderingServer.CanvasItemAddRect(_item, new Rect2(r.End.X - t, r.Position.Y + t, t, r.Size.Y - 2 * t), color);
    }

    internal static void DrawLabel(Rect2 screen, string text, Color color,
                                   TextSize size, LabelPlacement placement, int scale = 1)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var cell = Snap(screen);
        scale = RequireScale(scale);
        var font = Resolve(size, text, cell.Size, scale);
        var measured = (Vector2)font.Measure(text) * scale;

        // One font pixel of clearance, so a label is never flush against the edge it is
        // placed from and stays distinguishable from an outline on the same cell.
        var gap = scale;

        var origin = placement switch
        {
            LabelPlacement.TopLeft => new Vector2(cell.Position.X + gap, cell.Position.Y + gap),
            LabelPlacement.Above   => new Vector2(cell.GetCenter().X - measured.X / 2f,
                                                  cell.Position.Y - measured.Y - gap),
            LabelPlacement.Below   => new Vector2(cell.GetCenter().X - measured.X / 2f,
                                                  cell.End.Y + gap),
            _                      => cell.GetCenter() - measured / 2f,
        };

        // Whole pixels, so the nearest-neighbor filter has no fractional offset to resolve
        // and a glyph's blocks all come out the same size.
        origin = new Vector2(Mathf.Round(origin.X), Mathf.Round(origin.Y));

        var atlas = font.Atlas.GetRid();
        var lines = text.Split('\n');
        for (var line = 0; line < lines.Length; line++)
        for (var i = 0; i < lines[line].Length; i++)
        {
            var c = lines[line][i];
            if (c == ' ')
                continue;
            var dest = new Rect2(
                origin.X + i * font.Advance * scale,
                origin.Y + line * font.LineHeight * scale,
                font.GlyphWidth * scale,
                font.GlyphHeight * scale);
            RenderingServer.CanvasItemAddTextureRectRegion(_item, dest, atlas, font.Region(c), color);
        }
    }

    /// <summary>
    /// Rounds a rectangle to whole screen pixels. At a fractional zoom a cell's edges land
    /// between pixels, and an unsnapped fill bleeds a translucent line onto its neighbours,
    /// which reads as a mark on a cell nobody marked.
    /// </summary>
    private static Rect2 Snap(Rect2 r)
    {
        var min = new Vector2(Mathf.Round(r.Position.X), Mathf.Round(r.Position.Y));
        var max = new Vector2(Mathf.Round(r.End.X), Mathf.Round(r.End.Y));
        var size = max - min;
        return new Rect2(min, new Vector2(Mathf.Max(1f, size.X), Mathf.Max(1f, size.Y)));
    }
}

/// <summary>
/// Rebuilds the overlay once per frame, right after the game has rebuilt the world it sits
/// on top of.
///
/// Gameplay.Process is where the game positions the world sprite and computes its shader
/// offsets from the camera, so a postfix on it projects through exactly the camera the frame
/// was drawn with. Driving the overlay from SceneTree.ProcessFrame instead would be a frame
/// out of step whenever the camera is moving, and a mark a few pixels off the cell it names
/// is worse than no mark.
///
/// Deliberately not gated on a run being in progress: the overlay is as much for watching a
/// mod while playing it as for a test that screenshots it.
/// </summary>
[HarmonyPatch(typeof(Gameplay), nameof(Gameplay.Process))]
internal static class OverlayRedrawPatch
{
    private static void Postfix()
    {
        try
        {
            Overlay.Redraw();
        }
        catch (Exception ex)
        {
            // The overlay must never be the reason the game's own frame fails. A painter's
            // own failure is handled where it happens; this is the backstop for everything
            // else, and it disables the overlay rather than logging once per frame forever.
            Log.Error($"overlay redraw failed and the overlay has been reset: {ex}");
            Overlay.Reset();
        }
    }
}
