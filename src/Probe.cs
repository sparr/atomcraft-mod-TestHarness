using System.Reflection;

namespace Atomcraft.TestHarness;

/// <summary>
/// Reads game state that may not exist in every build.
///
/// The harness compiles against one copy of Atomcraft.dll but is expected to run against
/// whatever the player has installed, and the game's API is not stable across builds:
/// <c>Simulation.IsSimulationPaused</c>, for example, is absent from builds before
/// 2026-08-25 and present after. A direct reference makes the harness fail to *compile*
/// against an older build, which is a needlessly hard failure for a diagnostic field.
///
/// Anything the harness genuinely depends on should be referenced directly, so that a
/// breaking game update is a loud compile error. This is for the rest.
/// </summary>
public static class Probe
{
    /// <summary>Reads a public static property or field, or null when the build lacks it.</summary>
    public static object? Static(Type type, string member)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var property = type.GetProperty(member, flags);
            if (property != null)
                return property.GetValue(null);

            var field = type.GetField(member, flags);
            return field?.GetValue(null);
        }
        catch (Exception ex)
        {
            Log.Warn($"probe {type.Name}.{member} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when the running game exposes the member at all.</summary>
    public static bool Exists(Type type, string member)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        return type.GetProperty(member, flags) != null || type.GetField(member, flags) != null;
    }
}
