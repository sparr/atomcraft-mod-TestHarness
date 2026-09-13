using Atomcraft;
using Godot;

namespace Atomcraft.TestHarness;

/// <summary>
/// Puts the mouse where a test needs it, in world terms rather than screen ones.
/// </summary>
public static class Cursor
{
    /// <summary>
    /// Moves the mouse over a world tile and completes only once the game agrees: the wait
    /// finishes when Utils.GetTileMousePosition() has returned the target tile for
    /// <paramref name="settleFrames"/> consecutive frames.
    ///
    /// Warping alone is not hovering. The OS cursor moves immediately, but the game reads
    /// the mouse through the viewport a frame later, and the exact pixel mapping depends on
    /// window scaling the test cannot see. So this steers instead of solving: each frame it
    /// asks the game where it thinks the mouse is in world units, and corrects the warp
    /// point by the remaining error. Completing on observed agreement rather than on having
    /// warped is what lets the caller place one pixel and trust the box is reading it,
    /// instead of placing a block large enough to absorb the drift.
    ///
    /// Use after <see cref="View.LookAt"/> has anchored the view; over a moving view the
    /// tile under any point never settles, and this waits until the test times out, which
    /// is the honest outcome.
    ///
    ///     yield return View.LookAt(tile);
    ///     yield return Cursor.Hover(tile);
    /// </summary>
    public static Wait Hover(Vector2I tile, int settleFrames = 3) => new HoverWait(tile, settleFrames);

    private sealed class HoverWait : Wait
    {
        private readonly Vector2I _tile;
        private readonly int _settleFrames;
        private int _settled;
        private Vector2? _screen;
        private Vector2I? _lastSeen;

        public HoverWait(Vector2I tile, int settleFrames)
        {
            _tile = tile;
            _settleFrames = settleFrames;
        }

        public override bool Done => _settled >= _settleFrames;

        public override void Tick()
        {
            if (Game.World == null || Client.FollowCam == null)
                throw new AssertionException("no world or camera; is a session active?");

            // Aimed at the tile's center, so residual sub-pixel error stays inside the cell.
            var targetWorld = new Vector2(
                _tile.X * View.TileSize + View.TileSize / 2f,
                _tile.Y * View.TileSize + View.TileSize / 2f);

            if (_screen == null)
            {
                // First frame: the inverse of the game's own Utils.ScreenPositionToWorldPosition,
                // as a starting guess. Any constant scaling it misses is steered out below.
                var viewportSize = Game.CanvasLayer.GetViewport().GetVisibleRect().Size;
                _screen = (targetWorld - Client.FollowCam.GlobalPosition) * Client.FollowCam.Zoom
                          + viewportSize * 0.5f;
            }
            else
            {
                // Later frames: measure where the game saw the mouse land, in world units,
                // and move the warp point by the error. A proportional controller, so it
                // converges even when the true pixels-per-unit differs from Zoom by the
                // constant factor a scaled window introduces.
                var seenWorld = Game.World.GetGlobalMousePosition();
                var correction = (targetWorld - seenWorld) * Client.FollowCam.Zoom;
                _screen += correction;
            }

            // Off-window warps are silently clamped by the OS, which would stall the loop
            // against an edge; keep the point inside and let Describe say what happened.
            var window = (Vector2)DisplayServer.WindowGetSize();
            _screen = new Vector2(
                Mathf.Clamp(_screen.Value.X, 1f, window.X - 2f),
                Mathf.Clamp(_screen.Value.Y, 1f, window.Y - 2f));

            Input.WarpMouse(_screen.Value);

            _lastSeen = Utils.GetTileMousePosition();
            _settled = _lastSeen == _tile ? _settled + 1 : 0;
        }

        public override string Describe() =>
            $"the cursor to settle over tile {_tile}" +
            (_lastSeen is Vector2I seen && seen != _tile
                ? $" (the game last saw it over {seen}; is the view held? See View.LookAt. " +
                  "If the tiles differ wildly the view is moving; if only slightly, the tile " +
                  "may sit off-screen or under HUD at this window size)"
                : "");
    }
}
