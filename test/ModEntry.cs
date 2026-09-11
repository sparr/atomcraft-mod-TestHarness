using System.Collections.Generic;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Loader entry point. The harness finds tests by attribute across every loaded assembly,
/// so nothing is registered here beyond this mod's own per-cell channel.
/// </summary>
public static class ModEntry
{
    public static void Initialize()
    {
        // The loader's dependencies carry no version constraint, so a mismatched harness
        // would otherwise surface as a MissingMethodException once a test runs.
        Harness.RequireVersion("0.2");
        ExampleChannel.Register();
        Log.Info("TestHarness.Test loaded");
    }

    /// <summary>Called by the mod loader when a universe is saved. The return value is stored.</summary>
    public static Dictionary<string, int> OnUniverseSave()
    {
        var saved = ExampleChannel.Save();
        Log.Info($"persisting {saved.Count} cell(s) of {ExampleChannel.Name}");
        return saved;
    }

    /// <summary>Called by the mod loader when a universe is loaded, with what was stored.</summary>
    public static void OnUniverseLoad(Dictionary<string, int> saved)
    {
        ExampleChannel.Load(saved);
        Log.Info($"restored {saved?.Count ?? 0} cell(s) of {ExampleChannel.Name}");
    }
}
