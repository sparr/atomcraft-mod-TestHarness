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

    /// <summary>
    /// Loading a world replaces a channel rather than layering onto whatever was there.
    ///
    /// The half AssertFieldSurvivesSaveLoad cannot see. A load hook that restores saved
    /// state on top of existing state passes every round trip, because the state it should
    /// have cleared happens to be the state it is restoring. The bug only appears when a
    /// player leaves one world and loads another, and finds the first world's data sitting
    /// at the same coordinates.
    ///
    /// Writes into <paramref name="savedRect"/>, saves, writes into
    /// <paramref name="unsavedRect"/> afterwards, reloads, and asserts the first survived
    /// and the second is gone.
    /// </summary>
    public static IEnumerator AssertFieldIsReplacedOnLoad<T>(string fieldName,
        (int X, int Y, int Width, int Height) savedRect,
        (int X, int Y, int Width, int Height) unsavedRect,
        T marker)
    {
        if (!Session.Active)
            throw new AssertionException(
                $"AssertFieldIsReplacedOnLoad('{fieldName}') needs a session; yield Session.Enter first");

        var spec = FieldRegistry.Get<T>(fieldName);
        if (EqualityComparer<T>.Default.Equals(marker, spec.Unset))
            throw new AssertionException(
                $"the marker for '{fieldName}' is its unset value, so this would pass without " +
                "writing anything distinguishable");

        var saved = Capture(spec, savedRect.X, savedRect.Y, savedRect.Width, savedRect.Height);
        if (saved.Values.All(v => EqualityComparer<T>.Default.Equals(v, spec.Unset)))
            throw new AssertionException(
                $"nothing is set in the saved rectangle of '{fieldName}'. Write the state that " +
                "should survive before asserting a reload replaces the rest.");

        Session.Save();

        // Written after the save, so a correct load has no record of it and must drop it.
        for (var dy = 0; dy < unsavedRect.Height; dy++)
        for (var dx = 0; dx < unsavedRect.Width; dx++)
            spec.Put(unsavedRect.X + dx, unsavedRect.Y + dy, marker);

        // Session.Reload rather than Leave plus Enter: re-entering without clearing
        // FileManager's universe cache is served from memory, so the mod loader's restore
        // hook never runs. For a mod that clears its state when a world ends, which the
        // harness now does for every registered channel, that produces a false failure
        // reading as "your save hook is broken" rather than a false pass.
        yield return Session.Reload();

        var survived = Capture(spec, savedRect.X, savedRect.Y, savedRect.Width, savedRect.Height);
        var lost = saved.Count(kv => !EqualityComparer<T>.Default.Equals(kv.Value, survived[kv.Key]));

        var lingering = 0;
        for (var dy = 0; dy < unsavedRect.Height; dy++)
        for (var dx = 0; dx < unsavedRect.Width; dx++)
            if (EqualityComparer<T>.Default.Equals(spec.At(unsavedRect.X + dx, unsavedRect.Y + dy), marker))
                lingering++;

        Log.Event("persistence", new()
        {
            ["field"] = fieldName,
            ["check"] = "replaced_on_load",
            ["lost"] = lost,
            ["lingering"] = lingering,
        });

        if (lost > 0)
            throw new AssertionException(
                $"'{fieldName}' lost {lost} saved cell(s) across the reload");
        if (lingering > 0)
            throw new AssertionException(
                $"'{fieldName}' still holds {lingering} cell(s) written after the save. The load " +
                "hook is restoring on top of existing state rather than replacing it, which no " +
                "save round trip can catch and which shows up when a player switches worlds.");
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
