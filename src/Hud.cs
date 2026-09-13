using System.Reflection;
using Atomcraft;
using Godot;

namespace Atomcraft.TestHarness;

/// <summary>
/// Reads what the player sees, so a GUI test asserts on rendered UI without reflecting into
/// the game's private fields itself.
///
/// The point is not to save a test three lines of reflection; it is that the reflection lives
/// in exactly one place, with an error message that names what changed, when a game update
/// renames a field. A dozen tests each carrying their own copy would all break at once with a
/// NullReferenceException apiece.
/// </summary>
public static class Hud
{
    /// <summary>
    /// Whether the hover box, the game's under-the-cursor material readout, is currently
    /// shown. False covers every reason it might not be: no session, a window open, the
    /// cursor over HUD or fog, or nothing under the cursor worth describing.
    /// </summary>
    public static bool HoverBoxVisible =>
        Gameplay.Instance?.HoveredMaterialHint?.Visible ?? false;

    /// <summary>
    /// The text the hover box is showing right now. The box rebuilds this every frame from
    /// the tile under the mouse, so read it on the frame being asserted, after the cursor and
    /// camera have settled (see <see cref="View.LookAt"/> and <see cref="Cursor.Hover"/>).
    /// </summary>
    public static string HoverBoxText()
    {
        var hint = Gameplay.Instance?.HoveredMaterialHint
            ?? throw new AssertionException(
                "Gameplay.HoveredMaterialHint is not present; is a session active?");
        if (HoverBoxLabel(hint) is not Label label)
            throw new AssertionException(
                "HoveredMaterialHint no longer has a private Label field, so the hover box's " +
                "text cannot be read. A game update likely renamed it; Hud.HoverBoxText is " +
                "the one place to fix.");
        return label.Text;
    }

    private static object? HoverBoxLabel(HoveredMaterialHint hint) =>
        typeof(HoveredMaterialHint)
            .GetField("Label", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(hint);

    /// <summary>
    /// Whether the game still has the private field the hover box reader depends on. A test
    /// can check this headless, so a game update that renames the field fails the ordinary
    /// suite rather than only the occasional headful run.
    /// </summary>
    public static bool HoverBoxLabelFieldExists =>
        typeof(HoveredMaterialHint)
            .GetField("Label", BindingFlags.NonPublic | BindingFlags.Instance) != null;
}
