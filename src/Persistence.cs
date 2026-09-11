using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Atomcraft.TestHarness;

/// <summary>
/// Checks that a registered channel survives a save and reload.
///
/// This is the assertion mod state exists for. Persisting per-cell data means implementing
/// OnUniverseSave and OnUniverseLoad, whose failure mode is quiet: the mod keeps working,
/// the world keeps loading, and the state is simply gone or subtly wrong. Nothing complains.
///
/// It also covers the unstable-id hazard without a test having to think about it. Modded
/// material ids are assigned by load order, so anything persisted as a raw id comes back as
/// a different material when the installed mod set changes; a channel keyed or valued by id
/// will fail here rather than in a player's world.
/// </summary>
public static class Persistence
{
    /// <summary>
    /// Captures a channel over a rectangle, saves and reloads, and asserts it came back.
    ///
    /// Yield this from a frame-driven test after setting the state up. The test must
    /// already be in a session.
    /// </summary>
    public static IEnumerator AssertFieldSurvivesSaveLoad<T>(string fieldName,
        int x, int y, int width, int height)
    {
        if (!Session.Active)
            throw new AssertionException(
                $"AssertFieldSurvivesSaveLoad('{fieldName}') needs a session; yield Session.Enter first");

        var spec = FieldRegistry.Get<T>(fieldName);
        var before = Capture(spec, x, y, width, height);

        var set = before.Count(v => !EqualityComparer<T>.Default.Equals(v.Value, spec.Unset));
        if (set == 0)
            throw new AssertionException(
                $"every cell of '{fieldName}' in the given rectangle is unset, so this would " +
                "pass without testing anything. Write some state before asserting it survives.");

        yield return Session.SaveAndReload();

        var after = Capture(spec, x, y, width, height);
        var lost = new List<string>();
        foreach (var (key, value) in before)
        {
            after.TryGetValue(key, out var now);
            if (!EqualityComparer<T>.Default.Equals(value, now))
                lost.Add($"({key.X},{key.Y}): {value} -> {now}");
        }

        Log.Event("persistence", new()
        {
            ["field"] = fieldName,
            ["cells"] = before.Count,
            ["set"] = set,
            ["lost"] = lost.Count,
        });

        if (lost.Count > 0)
            throw new AssertionException(
                $"'{fieldName}' did not survive a save and reload: {lost.Count} of {set} written " +
                $"cell(s) changed.\n  " + string.Join("\n  ", lost.Take(8)) +
                (lost.Count > 8 ? $"\n  ... and {lost.Count - 8} more" : "") +
                "\n  Check the mod's OnUniverseSave and OnUniverseLoad, and that nothing is " +
                "persisted as a raw material id.");
    }

    private static Dictionary<(int X, int Y), T> Capture<T>(FieldSpec<T> spec,
        int x, int y, int width, int height)
    {
        var values = new Dictionary<(int X, int Y), T>();
        for (var dy = 0; dy < height; dy++)
        for (var dx = 0; dx < width; dx++)
            values[(x + dx, y + dy)] = spec.At(x + dx, y + dy);
        return values;
    }
}
