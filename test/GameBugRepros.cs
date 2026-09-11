using Atomcraft;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Regression cover for known game bugs. These assert what the game currently does, not
/// what it should do, so that the suite is honest about the ground it stands on. Each is
/// written to fail loudly when the bug is fixed, which is the signal to invert it.
/// </summary>
public static class GameBugRepros
{
    /// <summary>
    /// Reaction.MaxTemperature is declared, parsed from JSON, and copied into the runtime
    /// Reaction struct, but never read. Only the minimum is enforced, at
    /// BaseMaterial.cs:507, so a reaction fires at any temperature above its floor.
    ///
    /// Osmium Disulfide Decomposition is the clearest vanilla case: window 800 to 1100 K,
    /// Probability 0 so it fires the moment conditions are met with no dice roll, a
    /// single-pixel input, and no catalyst.
    ///
    /// No heating is needed. Ambient at the Deep band is already about 1573 K, some 470 K
    /// above the recipe's ceiling, so simply placing the material there is enough. An
    /// earlier version of this test built a chamber of heating elements and credited them
    /// with the temperature; they contributed roughly 40 K over ambient and the conclusion
    /// did not depend on them.
    ///
    /// Reported to the developer and independently reproduced in-game, 2026-09-10.
    /// </summary>
    [GameTest]
    public static void ReactionMaxTemperatureIsNotEnforced(Region r)
    {
        const string input = "Osmium Disulfide";
        const string product = "Osmium Tetroxide";
        const int ceiling = 1100;        // the recipe's stated MaxTemperature

        var ambient = r.AmbientAt(10);
        if (ambient <= ceiling)
            throw new AssertionException(
                $"this test assumes ambient here exceeds {ceiling} K, but it is {ambient} K. " +
                "The band or the planet's temperature profile has changed; pick a hotter band.");

        r.Fill(10, 10, 6, 3, input, ambient);
        var fired = r.TicksUntil(() => r.Count(product) > 0, 2000,
            $"the reaction fires at {ambient} K, {ambient - ceiling} K above its stated ceiling");

        Log.Info($"MaxTemperature unenforced: {input} decomposed at {ambient} K " +
                 $"(ceiling {ceiling} K) after {fired} tick(s)");
    }
}
