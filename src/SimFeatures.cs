using Atomcraft;
using HarmonyLib;

namespace Atomcraft.TestHarness;

/// <summary>
/// Parts of the game's simulation a test can switch off.
///
/// Isolation, mostly. A mod's own pass is hard to observe while gravity is rearranging the
/// scene underneath it, and a temperature is hard to hold while the ambient pull drags it
/// back. Turning one thing off is also how you find out which of two mechanisms produced a
/// result, without building a fixture elaborate enough to exclude one of them by geometry.
/// </summary>
[Flags]
public enum SimFeature
{
    None = 0,

    /// <summary>
    /// Falling, flowing, and rising: the movement a material does at the end of its own Step,
    /// after reactions and heat. Machines that move other pixels are unaffected, since that
    /// happens in their own Step override rather than here.
    /// </summary>
    Movement = 1,

    /// <summary>Heat spreading between neighboring cells, which the game runs every fifth tick.</summary>
    HeatConductance = 2,

    /// <summary>
    /// Heat decaying toward the planet's ambient temperature, on a position-hashed schedule.
    /// The reason a region set to 400 K drifts back toward 290 K if left alone, and what
    /// Region.PinnedHeat works around by rewriting the heat every tick. Switching this off
    /// addresses the cause instead.
    /// </summary>
    AmbientHeat = 4,

    /// <summary>Everything, for a test that wants the world to hold still.</summary>
    All = Movement | HeatConductance | AmbientHeat,
}

/// <summary>
/// Which parts of the simulation are currently switched off.
///
/// Patches are installed at load, before the game runs, and cost a static field read per pixel
/// per tick whether or not anything is disabled. See Install for why they are not deferred.
/// </summary>
public static class SimFeatures
{
    public static SimFeature Disabled { get; private set; }

    private static Harmony? _harmony;
    private static bool _patched;

    /// <summary>
    /// Installs the patches during Initialize, before the game has run a single tick.
    ///
    /// Deliberately not deferred until something is first disabled, which is what this did at
    /// first and which fails in a way worth recording: patching a method the runtime has
    /// already jitted into a hot caller does not reliably take effect, so the switch worked
    /// when its tests ran alone and silently did nothing once a few hundred ticks of other
    /// tests had warmed the simulation. It cost a static field read per pixel to be correct.
    /// </summary>
    internal static void Install(Harmony harmony)
    {
        _harmony = harmony;
        EnsurePatched();
    }

    /// <summary>
    /// Switches features off until the returned scope is disposed, restoring whatever was set
    /// before. Nests, so an inner scope does not clobber an outer one.
    ///
    ///     using (SimFeatures.Disable(SimFeature.Movement))
    ///         r.Ticks(10);
    /// </summary>
    public static IDisposable Disable(SimFeature features)
    {
        var previous = Disabled;
        Set(Disabled | features);
        return new Scope(previous);
    }

    /// <summary>Sets the disabled set outright. The executor uses this for [GameTest(Disable = ...)].</summary>
    public static void Set(SimFeature features) => Disabled = features;

    internal static void Reset() => Disabled = SimFeature.None;

    private static bool Off(SimFeature feature) => (Disabled & feature) != 0;

    /// <summary>
    /// Never removed once installed: unpatching mid-run would race a simulation that may be
    /// part-way through a tick, and would reintroduce the jit problem described above.
    /// </summary>
    private static void EnsurePatched()
    {
        if (_patched) return;

        var harmony = _harmony
            ?? throw new AssertionException(
                "SimFeatures was never given a Harmony instance, so it cannot disable anything. " +
                "ModEntry.Initialize should call SimFeatures.Install.");

        var self = typeof(SimFeatures);

        // Movement only. These are the tail of BaseMaterial.Step, so reactions, decay,
        // ignition, condensation, evaporation, combustion, and growth all still run.
        harmony.Patch(AccessTools.Method(typeof(BaseMaterial), nameof(BaseMaterial.StepSolid)),
            prefix: new HarmonyMethod(self, nameof(SkipFall)));
        harmony.Patch(AccessTools.Method(typeof(BaseMaterial), nameof(BaseMaterial.StepLiquid)),
            prefix: new HarmonyMethod(self, nameof(SkipFall)));
        harmony.Patch(AccessTools.Method(typeof(BaseMaterial), nameof(BaseMaterial.StepGas)),
            prefix: new HarmonyMethod(self, nameof(SkipGas)));

        harmony.Patch(AccessTools.Method(typeof(Heat), nameof(Heat.Conductance)),
            prefix: new HarmonyMethod(self, nameof(SkipConductance)));
        harmony.Patch(AccessTools.Method(typeof(Heat), nameof(Heat.AverageToAmbient)),
            prefix: new HarmonyMethod(self, nameof(SkipAmbient)));

        _patched = true;
        Log.Info("sim feature patches installed");
    }

    private static bool SkipFall(ref FallResult __result)
    {
        if (!Off(SimFeature.Movement)) return true;
        __result = FallResult.None;
        return false;
    }

    private static bool SkipGas(ref bool __result)
    {
        if (!Off(SimFeature.Movement)) return true;
        __result = false;
        return false;
    }

    private static bool SkipConductance() => !Off(SimFeature.HeatConductance);

    private static bool SkipAmbient() => !Off(SimFeature.AmbientHeat);

    private sealed class Scope : IDisposable
    {
        private readonly SimFeature _previous;
        public Scope(SimFeature previous) => _previous = previous;
        public void Dispose() => Set(_previous);
    }
}
