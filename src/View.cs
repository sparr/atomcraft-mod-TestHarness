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
}
