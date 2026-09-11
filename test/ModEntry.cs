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
        Harness.RequireVersion("0.3");
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

    /// <summary>
    /// How many times the mod loader has invoked this mod's load hook.
    ///
    /// The hook firing at all is the thing worth asserting. It hangs off
    /// FileManager.TryLoadUniverseFile, which a re-entry skips entirely when the universe is
    /// still cached, so for a long time it never ran and every persistence assertion in this
    /// suite passed on state that had not left memory.
    /// </summary>
    public static int LoadHookCalls { get; private set; }

    /// <summary>The payload the loader handed back on the most recent load.</summary>
    public static Dictionary<string, int>? LastLoadPayload { get; private set; }

    /// <summary>Called by the mod loader when a universe is loaded, with what was stored.</summary>
    public static void OnUniverseLoad(Dictionary<string, int> saved)
    {
        LoadHookCalls++;
        LastLoadPayload = saved;
        ExampleChannel.Load(saved);
        Log.Info($"restored {saved?.Count ?? 0} cell(s) of {ExampleChannel.Name}");
    }
}
