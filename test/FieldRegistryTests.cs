namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Proves the registry's generic path with a channel the harness knows nothing about,
/// stored the way a mod would plausibly store it.
///
/// The store here is a dictionary keyed by coordinate, holding entries only for cells that
/// have been written. That is the expected shape for mod state: structured, addressable by
/// (x, y), and nowhere near the size of the world. If the harness works against this it
/// works against a flat array too, since it only ever asks for the value at a cell.
/// </summary>
public static class FieldRegistryTests
{
    private const string Channel = "harness.example.pressure";

    private static readonly Dictionary<(int X, int Y), int> Store = new();
    private static bool _registered;

    /// <summary>
    /// Registration normally happens in a mod's Initialize(). Doing it lazily here keeps
    /// the example self-contained.
    /// </summary>
    private static void EnsureRegistered()
    {
        if (_registered)
            return;
        _registered = true;

        FieldRegistry.Register(new FieldSpec<int>
        {
            Name = Channel,
            Unset = 0,
            Read = (x, y) => Store.TryGetValue((x, y), out var v) ? v : 0,
            Write = (x, y, v) =>
            {
                if (v == 0)
                    Store.Remove((x, y));      // sparse stores do not keep empty entries
                else
                    Store[(x, y)] = v;
            },
            Clear = (x, y, w, h) =>
            {
                for (var dy = 0; dy < h; dy++)
                for (var dx = 0; dx < w; dx++)
                    Store.Remove((x + dx, y + dy));
            },
            Format = v => v.ToString(),
        });
    }

    [GameTest]
    public static void ASparseModFieldReadsAndWrites(Region r)
    {
        EnsureRegistered();
        var pressure = r.Field<int>(Channel);

        pressure.AssertUniform(0);

        pressure[3, 4] = 120;
        pressure[5, 6] = 40;

        pressure.AssertAt(3, 4, 120);
        pressure.AssertAt(5, 6, 40);
        pressure.AssertAt(0, 0, 0);

        if (pressure.CountSet() != 2)
            throw new AssertionException($"expected 2 set cells, found {pressure.CountSet()}\n{pressure.Dump()}");

        // A sparse store should hold entries only for what was written.
        if (Store.Count != 2)
            throw new AssertionException($"backing store holds {Store.Count} entries, expected 2");
    }

    /// <summary>
    /// The isolation guarantee. This test never writes to the channel, and must still see
    /// it empty even though the test above filled cells in its own region.
    /// </summary>
    [GameTest]
    public static void ModFieldsAreClearedBetweenTests(Region r)
    {
        EnsureRegistered();
        r.Field<int>(Channel).AssertUniform(0);
    }

    /// <summary>The game's own channels are registered through the same public call.</summary>
    [GameTest]
    public static void VanillaChannelsAreRegistered(Region r)
    {
        var heat = r.Field<short>("core.heat");
        heat.AssertUniform(290);

        heat[2, 2] = 500;
        heat.AssertAt(2, 2, 500);

        var material = r.Field<short>("core.material");
        material.AssertUniform(-1);

        r.Set(4, 4, "Granite");
        if (material[4, 4] == -1)
            throw new AssertionException($"core.material did not see the placed pixel\n{material.Dump()}");
    }
}
