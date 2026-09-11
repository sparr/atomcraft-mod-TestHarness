using Atomcraft;
using HarmonyLib;

namespace Atomcraft.TestHarness;

/// <summary>
/// Clears registered mod state when a world ends.
///
/// Simulation.Reset tears down the world's snapshot, which makes it the one reliable "this
/// world is over" signal. The mod loader's OnUniverseLoad is not a substitute: it runs only
/// when a universe file is actually read, so leaving one world and starting a freshly
/// generated one never calls it, and a mod inherits the previous world's state sitting on
/// whatever pixels happen to occupy those coordinates.
///
/// That failure is invisible to every save-and-reload test, which is why this is the
/// harness's job rather than advice in a document. A mod whose state should outlive a world,
/// configuration read once at startup for instance, simply does not register it, or registers
/// it with a Reset that keeps what should be kept.
///
/// Found in the Pressure mod, which patches the same method for the same reason.
/// </summary>
[HarmonyPatch(typeof(Simulation), nameof(Simulation.Reset))]
internal static class WorldEndReset
{
    private static void Postfix()
    {
        FieldRegistry.ResetAll();
        StateRegistry.ResetAll();
        Log.Info("world ended: registered mod channels and state reset");
    }
}
