using System.Reflection;
using Atomcraft;
using Godot;

namespace Atomcraft.TestHarness;

/// <summary>
/// Control over what is on screen, for tests that assert on live UI.
///
/// The game's view is built entirely around the avatar: the camera lerps toward it and is
/// leashed to within 200 world units of it every frame, and the rendered world window (which
/// is also the fog-of-war window) is computed from its tile. There is no way to look
/// somewhere the avatar is not, so LookAt does the only thing the game's own design permits:
/// it moves the avatar. Each of those facts is a piece of game internals no test should have
/// to know, which is why this class exists.
/// </summary>
public static class View
{
    private const string HoldName = "View.LookAt";

    /// <summary>Tiles are 8 world units on a side; see Utils.TileposToGlobal.</summary>
    internal const float TileSize = 8f;

    /// <summary>
    /// The camera aims 80 world units below the avatar (FollowCam.GetTargetGlobalPosition
    /// adds it), so anchoring the avatar this far above the target puts the camera's own
    /// target exactly on it, and the camera stops fighting the pin at all.
    /// </summary>
    private static readonly Vector2 CameraAimOffset = new(0f, 80f);

    /// <summary>
    /// Aims the view at a tile and holds it there for the rest of the test.
    ///
    /// Holding is the point, and it works by anchoring the avatar. Every frame until the
    /// test ends: any open window is re-closed (an open one pauses the game and hides
    /// under-cursor UI), the avatar is re-anchored just above the tile with its momentum
    /// zeroed (so gravity cannot accumulate a fall between frames), and the camera is set
    /// on the tile, which the game's own follow logic now agrees with since its target is
    /// the avatar. The hold is undone automatically when the test ends, or early by
    /// <see cref="Release"/>.
    ///
    /// The tile is also cleared of fog of war first (see <see cref="RevealFog"/>), since
    /// the point of looking at a cell is to see it, and several UI elements refuse to draw
    /// over fog.
    ///
    /// Yield the returned wait, so the held view has actually rendered before anything
    /// reads it:
    ///
    ///     yield return View.LookAt(tile);
    ///
    /// Best over open ground or sky. Anchoring the avatar inside solid material triggers
    /// the game's own unstick logic, which fights the hold.
    ///
    /// Place test pixels *after* this, not before. Aiming at a region the game has not yet
    /// activated makes it stream that region's planet segment in over anything written
    /// there beforehand. Once the view has settled the region is live and writes stay put;
    /// the per-frame hold re-clears fog around the tile but leaves solid material alone.
    /// </summary>
    public static Wait LookAt(Vector2I tile)
    {
        if (Client.FollowCam == null)
            throw new AssertionException("no camera to aim; is a session active?");

        RevealFog(tile);

        // Center of the tile, not its corner, so the cell sits centered on screen and a
        // subsequent Cursor.Hover lands in its middle rather than on an edge.
        var target = new Vector2(tile.X * TileSize + TileSize / 2f, tile.Y * TileSize + TileSize / 2f);

        void Hold()
        {
            Session.CloseAllWindows();
            if (GodotObject.IsInstanceValid(Avatars.LocalAvatar))
            {
                Avatars.LocalAvatar.GlobalPosition = target - CameraAimOffset;
                ZeroMomentum(Avatars.LocalAvatar);
            }
            Client.FollowCam.GlobalPosition = target;
        }

        Hold();
        FrameHolds.Set(HoldName, Hold);

        // Two frames: one for the held view to render, one for everything that reads the
        // rendered frame (the hover box among them) to catch up. Cursor.Hover then waits
        // for genuine agreement, so nothing downstream depends on this being exact.
        return Wait.Frames(2);
    }

    /// <summary>Stops holding the view. Rarely needed; every hold ends with its test.</summary>
    public static void Release() => FrameHolds.Remove(HoldName);

    /// <summary>
    /// Zeroes the avatar's accumulated momentum, so a re-anchored avatar does not carry a
    /// growing fall into the next frame. The field is the game's own (Actor.Momentum) and
    /// protected, so this is reflection; losing it degrades the hold to sub-tile jitter
    /// rather than breaking it, hence a warning and not a failure.
    /// </summary>
    private static readonly FieldInfo? MomentumField =
        typeof(Actor).GetField("Momentum", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool _warnedNoMomentum;

    private static void ZeroMomentum(Avatar avatar)
    {
        if (MomentumField != null)
        {
            MomentumField.SetValue(avatar, Vector2.Zero);
        }
        else if (!_warnedNoMomentum)
        {
            _warnedNoMomentum = true;
            Log.Warn("Actor.Momentum is gone; View.LookAt still holds, with slightly more " +
                     "per-frame jitter, but the game changed shape and View needs updating");
        }
    }

    /// <summary>
    /// Clears fog of war around a tile, so the things that refuse to draw over fog will draw.
    ///
    /// Fog of war is not UI state: it is fog material in the simulation field, and the game
    /// derives the on-screen fog mask from the materials in view each frame. Revealing a
    /// spot therefore means replacing its fog pixels with air, which is a world edit like
    /// any other test setup.
    ///
    /// The radius reaches well past the asked-for tile because the mask is blurred before
    /// use: a cell reads as fogged when enough fog sits within a few cells of it, so a
    /// pinhole reveal of exactly one cell would still be covered by its neighbors' blur.
    /// </summary>
    public static void RevealFog(Vector2I tile, int radius = 16)
    {
        var field = Simulation.CurrentState?.Field
            ?? throw new AssertionException("no simulation field; is the game loaded?");

        for (var y = Math.Max(0, tile.Y - radius); y <= Math.Min(field.Height - 1, tile.Y + radius); y++)
        for (var x = Math.Max(0, tile.X - radius); x <= Math.Min(field.Width - 1, tile.X + radius); x++)
        {
            if (!Materials.IsFogOfWarMaterial(field.Get(x, y)))
                continue;
            field.Set(x, y, -1);
            Session.MarkDirty(x, y);
        }
    }

    /// <summary>
    /// Dismisses the mod loader's load report, the full-screen panel it puts up over the game
    /// at startup, exactly as pressing its Continue button would.
    ///
    /// It is an opaque CanvasLayer above the whole game, so until something dismisses it every
    /// headful screenshot is a picture of the report rather than of the mod under test. In a
    /// suite run there is nobody to press Continue, so the harness does it when the run
    /// starts; a mod driving the game itself can call this.
    ///
    /// Nothing is lost by dismissing it. The report only restates what the loader already
    /// wrote to godot.log, and the harness checks the same thing itself, more strictly, in
    /// its installed-mod validation.
    ///
    /// Returns whether a report was found and dismissed, so a caller can tell "gone" from
    /// "never there", and is harmless to call when there is none.
    /// </summary>
    public static bool DismissModLoaderReport()
    {
        var root = Game.Instance?.GetTree()?.Root;
        if (root == null)
            return false;

        foreach (var child in root.GetChildren())
        {
            // Matched on the scene it was instantiated from first, and on its node name only
            // as a fallback: the name is the loader's to change, the scene file is the thing
            // that identifies it.
            if (child is not CanvasLayer layer)
                continue;
            if (!layer.SceneFilePath.EndsWith("ModLoaderReport.tscn", StringComparison.OrdinalIgnoreCase)
                && layer.Name != "LoadingReport")
                continue;

            // Free rather than hide, which is what the Continue button itself does, so the
            // game is left in the state a player would have left it in.
            layer.QueueFree();
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- zoom

    /// <summary>
    /// The furthest in the shipped game will zoom: <c>FollowCam.IncreaseZoom</c> clamps its
    /// target here. One cell is 8 world units, so at this zoom a cell is 12 screen pixels.
    /// </summary>
    public const float GameMaxZoom = 1.5f;

    /// <summary>
    /// The largest multiple of <see cref="GameMaxZoom"/> <see cref="MaxZoomFactor"/> accepts.
    ///
    /// Nothing in the game breaks past it; the render window is sized in cells and does not
    /// depend on zoom, so zooming in only shows fewer of them. The ceiling is here so that a
    /// mistyped factor leaves someone looking at a readable view rather than at the inside of
    /// one pixel. At 8, a cell is 96 screen pixels: about twenty cells across a 1920-wide
    /// window, and room for a dozen characters of the smallest overlay text inside one cell.
    /// </summary>
    public const int MaxZoomFactorLimit = 8;

    private static int _maxZoomFactor = 1;

    /// <summary>
    /// How much further than the game's own limit the camera may zoom in, as a multiple of
    /// <see cref="GameMaxZoom"/>. 1 (the default) leaves the game exactly as shipped.
    ///
    ///     View.MaxZoomFactor = 4;      // the zoom-in key now reaches 6.0 instead of 1.5
    ///
    /// This raises the ceiling; it does not move the camera. The player's own zoom keys keep
    /// working, at the game's own speed and with the game's own easing, and simply stop
    /// later. Use <see cref="SetZoom"/> to go somewhere specific right now.
    ///
    /// Zooming out is untouched: the lower limit exists because below it the camera would see
    /// past the edge of the rendered window, which is a real constraint and not an arbitrary
    /// one.
    /// </summary>
    public static int MaxZoomFactor
    {
        get => _maxZoomFactor;
        set
        {
            if (value < 1 || value > MaxZoomFactorLimit)
                throw new AssertionException(
                    $"MaxZoomFactor must be between 1 and {MaxZoomFactorLimit}; got {value}");
            if (value > 1 && TargetZoomField == null)
                throw new AssertionException(
                    "FollowCam no longer has its private TargetZoom field, so the zoom limit " +
                    "cannot be raised. A game update likely renamed it; View is the one place to fix.");
            _maxZoomFactor = value;
            ClampZoomToLimit();
        }
    }

    /// <summary>
    /// Brings the camera back inside the current limit, so that lowering the limit is not a
    /// limit the camera is already past.
    ///
    /// This matters most where it is least visible: a test that raises the limit, zooms in,
    /// and puts the limit back leaves the camera zoomed further than the next test's limit
    /// allows, and the next test measures a view it never asked for. Silent, and only wrong
    /// sometimes, which is the worst combination.
    /// </summary>
    private static void ClampZoomToLimit()
    {
        var cam = Client.FollowCam;
        if (cam == null || cam.Zoom.X <= MaxZoom)
            return;
        cam.Zoom = new Vector2(MaxZoom, MaxZoom);
        if (TargetZoomField != null)
            TargetZoomField() = MaxZoom;
    }

    /// <summary>The furthest in the camera will now go: <see cref="GameMaxZoom"/> times <see cref="MaxZoomFactor"/>.</summary>
    public static float MaxZoom => GameMaxZoom * _maxZoomFactor;

    /// <summary>How much the raised ceiling adds. Zero when the limit is left as the game ships it.</summary>
    internal static float ZoomHeadroom => MaxZoom - GameMaxZoom;

    /// <summary>Where the camera is zoomed right now. One cell is <c>8 * Zoom</c> screen pixels.</summary>
    public static float Zoom =>
        Client.FollowCam?.Zoom.X
        ?? throw new AssertionException("no camera to read the zoom from; is a session active?");

    /// <summary>
    /// Puts the camera at a specific zoom immediately, rather than easing toward it.
    ///
    ///     yield return View.SetZoom(6f);
    ///
    /// The game eases zoom in by a tenth of the remaining distance per frame, which is right
    /// for a player holding a key and wrong for a test that wants a known view on a known
    /// frame, so this sets the camera and its easing target together and the easing has
    /// nothing left to do. Yield the returned wait so the new zoom has rendered before
    /// anything reads or screenshots it.
    ///
    /// Raise <see cref="MaxZoomFactor"/> first if you want past <see cref="GameMaxZoom"/>;
    /// asking for more than <see cref="MaxZoom"/> is refused rather than silently clamped,
    /// because a test that quietly got a different view than it asked for is a test whose
    /// screenshot means something other than it says.
    ///
    /// Not held. Nothing in ordinary play moves the zoom on its own, so it stays put until
    /// the player's zoom keys or a simulation-area change (which resets the camera to its
    /// minimum) move it.
    /// </summary>
    public static Wait SetZoom(float zoom)
    {
        var cam = Client.FollowCam
            ?? throw new AssertionException("no camera to zoom; is a session active?");

        if (zoom > MaxZoom + 0.0001f)
            throw new AssertionException(
                $"asked for zoom {zoom}, but the limit is {MaxZoom} " +
                $"({GameMaxZoom} x MaxZoomFactor {MaxZoomFactor}). Raise View.MaxZoomFactor first.");
        if (zoom <= 0f)
            throw new AssertionException($"zoom must be positive; got {zoom}");

        cam.Zoom = new Vector2(zoom, zoom);
        if (TargetZoomField != null)
            TargetZoomField() = zoom;

        return Wait.Frames(1);
    }

    /// <summary>
    /// FollowCam's private easing target. Both the zoom limit and <see cref="SetZoom"/> need
    /// it: setting Camera2D.Zoom alone is undone on the next frame, because FollowCam.Process
    /// eases the camera back toward this field every frame.
    /// </summary>
    internal static readonly HarmonyLib.AccessTools.FieldRef<float>? TargetZoomField =
        HarmonyLib.AccessTools.Field(typeof(FollowCam), "TargetZoom") is { } field
            ? HarmonyLib.AccessTools.StaticFieldRefAccess<float>(field)
            : null;

    /// <summary>
    /// Whether the game still has the private field the zoom control depends on, checkable
    /// from a headless test so a game update that renames it fails the ordinary suite.
    /// </summary>
    public static bool ZoomTargetFieldExists => TargetZoomField != null;

    /// <summary>
    /// Where the camera's zoom is heading. <see cref="Zoom"/> eases toward this by a tenth of
    /// the remaining distance each frame, so the two differ for a moment after the player
    /// works the zoom keys and agree the rest of the time.
    /// </summary>
    public static float ZoomTarget => TargetZoomField != null ? TargetZoomField() : Zoom;

    // ------------------------------------------------- world to screen

    /// <summary>How many screen pixels a simulation cell covers right now.</summary>
    public static float CellScreenSize => TileSize * Zoom;

    /// <summary>
    /// Where a cell's center is on screen, in viewport pixels, for anything a test wants to
    /// draw at or measure against.
    ///
    /// This is the inverse of the game's own <c>Utils.ScreenPositionToWorldPosition</c>, and
    /// so it agrees with the mapping the game uses to decide which cell the mouse is over.
    /// The coordinates are viewport pixels, which is the space a CanvasLayer draws in and the
    /// space <c>Game.World.GetGlobalMousePosition</c> reports; the OS window can be a scaled
    /// copy of the viewport, which is why <see cref="Cursor.Hover"/> steers the real cursor
    /// by observation instead of computing a warp point from this.
    ///
    /// A cell off screen still gets an answer, outside the viewport rect. Ask
    /// <see cref="IsVisible"/> if that matters.
    /// </summary>
    public static Vector2 ScreenOf(Vector2I tile) =>
        WorldToScreen(new Vector2(tile.X * TileSize + TileSize / 2f, tile.Y * TileSize + TileSize / 2f));

    /// <summary>The rectangle a cell covers on screen, in viewport pixels.</summary>
    public static Rect2 ScreenRectOf(Vector2I tile)
    {
        var topLeft = WorldToScreen(new Vector2(tile.X * TileSize, tile.Y * TileSize));
        return new Rect2(topLeft, new Vector2(CellScreenSize, CellScreenSize));
    }

    /// <summary>A world position in viewport pixels. The building block the rest of this section uses.</summary>
    public static Vector2 WorldToScreen(Vector2 world)
    {
        var cam = Client.FollowCam
            ?? throw new AssertionException("no camera to project through; is a session active?");
        return (world - cam.GlobalPosition) * cam.Zoom + ViewportSize * 0.5f;
    }

    /// <summary>Which cell a viewport position falls in. The game's own inverse, for round-tripping.</summary>
    public static Vector2I TileAt(Vector2 screen) => screen.ScreenPositionToWorldPosition().GlobalToTileposI();

    /// <summary>Whether a cell is currently within <see cref="VisibleTiles"/>.</summary>
    public static bool IsVisible(Vector2I tile) => VisibleTiles.Contains(tile);

    /// <summary>
    /// Every cell the player can actually see right now, edge cells included even when only
    /// part of one is on screen.
    ///
    /// Three things bound this, and all three matter. The viewport and the zoom say how much
    /// world fits on screen. The game renders the world from a fixed-size window of cells
    /// around the avatar, and outside that window there is nothing drawn to look at. And the
    /// simulation field has edges, past which there are no cells at all. The intersection is
    /// what is on screen and real, which is what a per-pixel pass wants to walk.
    /// </summary>
    public static RectInt VisibleTiles
    {
        get
        {
            var cam = Client.FollowCam
                ?? throw new AssertionException("no camera; is a session active?");
            var simField = Simulation.CurrentState?.Field
                ?? throw new AssertionException("no simulation field; is the game loaded?");
            if (!GodotObject.IsInstanceValid(Avatars.LocalAvatar))
                throw new AssertionException(
                    "no local avatar, so the game cannot say which window of cells it is " +
                    "rendering; is a session active?");

            var halfWorld = ViewportSize * 0.5f / cam.Zoom;
            var min = (cam.GlobalPosition - halfWorld).GlobalToTileposI();
            var max = (cam.GlobalPosition + halfWorld).GlobalToTileposI();
            var onScreen = new RectInt(min.X, min.Y, max.X - min.X + 1, max.Y - min.Y + 1);

            var rendered = Gameplay.GetWindowRectAndOriginForRenderingWorld().Item1;
            var world = new RectInt(0, 0, simField.Width, simField.Height);

            return onScreen.Intersection(rendered).Intersection(world);
        }
    }

    private static Vector2 ViewportSize =>
        Game.CanvasLayer?.GetViewport().GetVisibleRect().Size
        ?? throw new AssertionException("no viewport; is the game loaded?");
}

/// <summary>
/// Lets the camera zoom in past the game's own limit, by exactly the headroom
/// <see cref="View.MaxZoomFactor"/> asks for.
///
/// FollowCam.IncreaseZoom advances its easing target by a fixed speed and then clamps it to
/// 1.5. Rather than reimplement that (and silently inherit a stale copy of the speed constant
/// if the game ever retunes it), this biases the target down by the headroom before the
/// original runs and adds it back after. The original's own arithmetic and its own clamp then
/// produce min(target + speed * elapsed, 1.5 + headroom), which is the raised limit exactly.
///
/// DecreaseZoom is deliberately not patched: its lower clamp is the zoom below which the
/// camera would see past the edge of the rendered window.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(FollowCam), nameof(FollowCam.IncreaseZoom))]
internal static class ZoomLimitPatch
{
    private static void Prefix()
    {
        if (View.MaxZoomFactor > 1 && View.TargetZoomField != null)
            View.TargetZoomField() -= View.ZoomHeadroom;
    }

    private static void Postfix()
    {
        if (View.MaxZoomFactor > 1 && View.TargetZoomField != null)
            View.TargetZoomField() += View.ZoomHeadroom;
    }
}
