using System.Collections.Generic;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// A per-cell channel this mod owns and persists.
///
/// It exists so the harness's save and reload helper has something to prove itself
/// against, and it lives here rather than in the harness for the same reason the tests do:
/// a consumer registering and persisting its own state is exactly what it demonstrates.
///
/// Doubles as the worked example of the mod loader's save hooks: a static OnUniverseSave on
/// the module's init class returns whatever should be stored, and OnUniverseLoad receives it
/// back. Values are keyed by coordinate and stored sparsely, which is what mod state
/// actually looks like.
/// </summary>
public static class ExampleChannel
{
    public const string Name = "harness.example";

    private static Dictionary<string, int> _store = new();

    public static void Register()
    {
        FieldRegistry.Register(new FieldSpec<int>
        {
            Name = Name,
            Unset = 0,
            Read = (x, y) => _store.TryGetValue(Key(x, y), out var v) ? v : 0,
            Write = (x, y, v) =>
            {
                if (v == 0) _store.Remove(Key(x, y));
                else _store[Key(x, y)] = v;
            },
            Clear = (x, y, w, h) =>
            {
                for (var dy = 0; dy < h; dy++)
                for (var dx = 0; dx < w; dx++)
                    _store.Remove(Key(x + dx, y + dy));
            },
            // World-scoped: a world ending must drop it, or the next world inherits values
            // sitting on whatever pixels happen to occupy those coordinates. The load hook is
            // not a substitute, since it only runs when a universe file is actually read.
            ClearWorld = () => _store.Clear(),
            Format = v => v.ToString(),
        });
    }

    // A string key, because the saved blob is JSON and a tuple key would not round trip.
    private static string Key(int x, int y) => $"{x},{y}";

    internal static Dictionary<string, int> Save() => new(_store);

    internal static void Load(Dictionary<string, int>? saved) => _store = saved ?? new();
}
