namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Shared storage behind several channels, and the fast path for checksumming it.
///
/// Both exist for the same mod shape: thirteen registered channels that are views of one
/// store. Clearing each independently re-clears what the previous call already did, and
/// checksumming each cell by cell through a delegate is thirteen passes over the region.
/// </summary>
public static class FieldGroupTests
{
    private static int _clearRectCalls;
    private static int _clearEverythingCalls;
    private static readonly Dictionary<(int, int), int> Store = new();

    private static FieldGroup MakeGroup(string name) => new()
    {
        Name = name,
        ClearRect = (x, y, w, h) =>
        {
            _clearRectCalls++;
            for (var dy = 0; dy < h; dy++)
            for (var dx = 0; dx < w; dx++)
                Store.Remove((x + dx, y + dy));
        },
        ClearEverything = () => { _clearEverythingCalls++; Store.Clear(); },
    };

    private static FieldSpec<int> View(string name, FieldGroup group) => new()
    {
        Name = name,
        Unset = 0,
        Group = group,
        Read = (x, y) => Store.TryGetValue((x, y), out var v) ? v : 0,
        Write = (x, y, v) => Store[(x, y)] = v,
    };

    /// <summary>
    /// Three channels sharing one group clear the storage once, not three times. Without the
    /// group this is three passes over the same cells, two of them clearing what the first
    /// already cleared.
    /// </summary>
    [GameTest]
    public static void GroupedChannelsClearOnce(Region r)
    {
        var group = MakeGroup("harness.group");
        var names = new[] { "harness.group.a", "harness.group.b", "harness.group.c" };

        foreach (var name in names) FieldRegistry.Register(View(name, group));
        try
        {
            Store[(r.OriginX, r.OriginY)] = 5;
            _clearRectCalls = 0;

            FieldRegistry.ClearAll(r.OriginX, r.OriginY, r.Width, r.Height);

            if (_clearRectCalls != 1)
                throw new AssertionException(
                    $"expected the group to clear once for all three channels, got " +
                    $"{_clearRectCalls} call(s)");

            if (Store.ContainsKey((r.OriginX, r.OriginY)))
                throw new AssertionException("clearing once did not actually clear the storage");
        }
        finally
        {
            foreach (var name in names) FieldRegistry.Unregister(name);
        }
    }

    /// <summary>The same for the world-scoped reset that runs between tests.</summary>
    [GameTest]
    public static void GroupedChannelsResetOnce()
    {
        var group = MakeGroup("harness.group.reset");
        var names = new[] { "harness.reset.a", "harness.reset.b" };

        foreach (var name in names) FieldRegistry.Register(View(name, group));
        try
        {
            Store[(1, 1)] = 9;
            _clearEverythingCalls = 0;

            FieldRegistry.ResetAll();

            if (_clearEverythingCalls != 1)
                throw new AssertionException(
                    $"expected one group reset for two channels, got {_clearEverythingCalls}");

            if (Store.Count != 0)
                throw new AssertionException("the group reset did not empty the storage");
        }
        finally
        {
            foreach (var name in names) FieldRegistry.Unregister(name);
        }
    }

    /// <summary>
    /// A channel that owns its storage still clears on its own, so grouping is opt-in rather
    /// than a change in behavior for everyone.
    /// </summary>
    [GameTest]
    public static void UngroupedChannelsStillClearIndividually(Region r)
    {
        var calls = 0;
        const string name = "harness.ungrouped";

        FieldRegistry.Register(new FieldSpec<int>
        {
            Name = name,
            Unset = 0,
            Read = (_, _) => 0,
            Clear = (_, _, _, _) => calls++,
        });

        try
        {
            FieldRegistry.ClearAll(r.OriginX, r.OriginY, r.Width, r.Height);

            if (calls != 1)
                throw new AssertionException($"expected one clear, got {calls}");
        }
        finally
        {
            FieldRegistry.Unregister(name);
        }
    }

    /// <summary>
    /// Clear was a required property before groups existed, so the compiler caught a channel
    /// that resets nothing. It is optional now, which moves the check to registration. Without
    /// this, such a channel would leak state into every later test.
    /// </summary>
    [GameTest]
    public static void AChannelThatClearsNothingIsRefused()
    {
        try
        {
            FieldRegistry.Register(new FieldSpec<int>
            {
                Name = "harness.clearsnothing",
                Unset = 0,
                Read = (_, _) => 0,
            });

            FieldRegistry.Unregister("harness.clearsnothing");
            throw new AssertionException(
                "a channel with neither Clear nor Group should have been refused");
        }
        catch (AssertionException e) when (e.Message.Contains("neither Clear nor Group"))
        {
            // expected
        }
    }

    /// <summary>
    /// The checksum fast path is used when supplied. A mod with a flat backing array can serve
    /// this from the array instead of walking the rectangle through a per-cell delegate.
    /// </summary>
    [GameTest]
    public static void ASuppliedChecksumReplacesThePerCellWalk(Region r)
    {
        const string name = "harness.fastchecksum";
        const int sentinel = 8675309;
        var calls = 0;

        FieldRegistry.Register(new FieldSpec<int>
        {
            Name = name,
            Unset = 0,
            Read = (_, _) => throw new AssertionException(
                "the per-cell Read was called even though a Checksum was supplied"),
            Clear = (_, _, _, _) => { },
            Checksum = (_, _, _, _) => { calls++; return sentinel; },
        });

        try
        {
            var sums = FieldRegistry.ChecksumAll(r.OriginX, r.OriginY, r.Width, r.Height);

            if (!sums.TryGetValue(name, out var sum) || sum != sentinel)
                throw new AssertionException(
                    $"expected the supplied checksum {sentinel}, got " +
                    (sums.TryGetValue(name, out var got) ? got.ToString() : "no entry"));

            if (calls != 1)
                throw new AssertionException($"expected one checksum call, got {calls}");
        }
        finally
        {
            FieldRegistry.Unregister(name);
        }
    }

    /// <summary>
    /// Without a supplied checksum the per-cell walk still happens, so the fast path is an
    /// optimization rather than a new requirement.
    /// </summary>
    [GameTest]
    public static void AChannelWithoutAChecksumStillGetsOne(Region r)
    {
        const string name = "harness.slowchecksum";
        var reads = 0;

        FieldRegistry.Register(new FieldSpec<int>
        {
            Name = name,
            Unset = 0,
            Read = (_, _) => { reads++; return 1; },
            Clear = (_, _, _, _) => { },
        });

        try
        {
            FieldRegistry.ChecksumAll(r.OriginX, r.OriginY, 4, 4);

            if (reads == 0)
                throw new AssertionException(
                    "the default checksum did not read any cells, so it is not checksumming " +
                    "anything");
        }
        finally
        {
            FieldRegistry.Unregister(name);
        }
    }
}
