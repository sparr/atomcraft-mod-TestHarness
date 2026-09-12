namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Switching parts of the simulation off, so a test can watch one mechanism without another
/// rearranging the scene underneath it.
///
/// Each test here proves both directions. Asserting only that something stopped happening
/// would pass equally well against a fixture where it never happened.
/// </summary>
public static class SimFeatureTests
{
    [GameTest(Wall = "Granite", Disable = SimFeature.Movement)]
    public static void DisablingMovementStopsSandFalling(Region r)
    {
        r.Set(10, 10, "Sand");
        r.Ticks(30);

        r.AssertAt(10, 10, "Sand");
    }

    /// <summary>The same fixture with movement on, so the test above is not measuring a stuck grain.</summary>
    [GameTest(Wall = "Granite")]
    public static void TheSameSandFallsWhenMovementIsOn(Region r)
    {
        r.Set(10, 10, "Sand");
        r.Ticks(30);

        if (r.At(10, 10) == "Sand")
            throw new AssertionException(
                $"sand did not fall with movement enabled, so the disabled case proves nothing\n{r.Dump()}");
    }

    /// <summary>
    /// Ambient pull is what makes a heated region drift back toward the planet's temperature,
    /// and what Region.PinnedHeat papers over by rewriting heat every tick. With it off, a
    /// temperature simply stays.
    /// </summary>
    [GameTest(Wall = "Granite", Disable = SimFeature.AmbientHeat | SimFeature.HeatConductance)]
    public static void DisablingAmbientPullHoldsATemperature(Region r)
    {
        r.Fill(10, 10, 4, 4, "Granite", 400);
        r.Ticks(200);

        var held = r.HeatAt(11, 11);
        if (held != 400)
            throw new AssertionException(
                $"expected 400 K to hold with ambient pull and conductance off, got {held} K");
    }

    /// <summary>
    /// The same scene with everything on. Ambient here is well above 400 K, so the pull is
    /// upward; what matters is that it moves at all, which is what the test above suppresses.
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void TheSameTemperatureMovesWhenAmbientPullIsOn(Region r)
    {
        r.Fill(10, 10, 4, 4, "Granite", 400);
        r.Ticks(200);

        if (r.HeatAt(11, 11) == 400)
            throw new AssertionException(
                "the temperature did not move with ambient pull enabled, so the disabled case " +
                "proves nothing. Check the region's ambient, which must differ from 400 K.");
    }

    /// <summary>
    /// Conductance alone, with the ambient pull left off so it cannot account for the change.
    /// A hot cell beside cold ones spreads its heat; with conductance off it does not.
    /// </summary>
    [GameTest(Wall = "Granite", Disable = SimFeature.AmbientHeat)]
    public static void ConductanceCanBeDisabledOnItsOwn(Region r)
    {
        r.Fill(10, 10, 5, 1, "Granite", 300);
        r.SetHeat(12, 10, 900);

        using (SimFeatures.Disable(SimFeature.HeatConductance))
            r.Ticks(60);

        if (r.HeatAt(11, 10) != 300)
            throw new AssertionException(
                $"heat spread with conductance disabled: neighbor is {r.HeatAt(11, 10)} K");

        // Same scene, conductance back on: the neighbor must warm, or the assertion above is
        // about a scene where nothing would have spread anyway.
        r.Ticks(60);

        if (r.HeatAt(11, 10) == 300)
            throw new AssertionException(
                $"heat did not spread once conductance was re-enabled\n{r.Dump("core.heat")}");
    }

    /// <summary>A scope restores what was set before it, rather than clearing everything.</summary>
    [GameTest(Disable = SimFeature.Movement)]
    public static void AScopeRestoresThePreviousSetting()
    {
        if (SimFeatures.Disabled != SimFeature.Movement)
            throw new AssertionException(
                $"the attribute should have disabled Movement, got {SimFeatures.Disabled}");

        using (SimFeatures.Disable(SimFeature.AmbientHeat))
        {
            if (SimFeatures.Disabled != (SimFeature.Movement | SimFeature.AmbientHeat))
                throw new AssertionException(
                    $"a scope should add to what was set, got {SimFeatures.Disabled}");
        }

        if (SimFeatures.Disabled != SimFeature.Movement)
            throw new AssertionException(
                $"the scope should have restored Movement only, got {SimFeatures.Disabled}");
    }

    /// <summary>
    /// Nothing is disabled by default. This is what catches a previous test leaking its
    /// setting, which would quietly change what every later test measures.
    /// </summary>
    [GameTest]
    public static void NothingIsDisabledByDefault()
    {
        if (SimFeatures.Disabled != SimFeature.None)
            throw new AssertionException(
                $"a previous test left {SimFeatures.Disabled} disabled");
    }
}
