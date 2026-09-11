using Atomcraft;
using Newtonsoft.Json;

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
    /// Reaction struct, but never read. Only the minimum is enforced, so a reaction fires at
    /// any temperature above its floor.
    ///
    /// Not a defect, as it turns out: the developer says the ceiling is deliberately
    /// disabled at present, and hopefully temporarily. Kept here because the effect on a mod
    /// is the same either way, a declared ceiling does nothing, and because this test is how
    /// we will notice the day it is switched back on.
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

    /// <summary>
    /// MaterialType's constructor from Serializable_MaterialType assigns DissolvesInto =
    /// null and never reads the serializable field, so the value cannot be set from data.
    ///
    /// All three data paths share this constructor: the game's own AllMaterials.json, the
    /// user's user://Materials/ overrides, and a mod's materials JSON injected by
    /// GodotMonoModLoader. So testing the constructor covers all of them.
    ///
    /// Note the reverse direction works: Serializable_MaterialType's constructor converts
    /// the short[] back to names, so the field is written out and never read back in.
    /// </summary>
    [GameTest]
    public static void DissolvesIntoCannotBeSetFromData()
    {
        // Deserialized from text rather than constructed, so the test covers Newtonsoft
        // reading the field as well as the constructor discarding it.
        const string json = """
            [{ "Name": "TestHarness Dissolve Probe",
               "DissolvesInto": [ "Water", "Oxygen Gas" ] }]
            """;

        var serializable = JsonConvert.DeserializeObject<List<Serializable_MaterialType>>(json)![0];

        if (serializable.DissolvesInto is not { Length: 2 })
            throw new AssertionException(
                "the JSON itself did not parse, so this test would prove nothing about " +
                "MaterialType. Serializable_MaterialType.DissolvesInto came back as " +
                (serializable.DissolvesInto == null ? "null" : $"{serializable.DissolvesInto.Length} entries"));

        var materialType = new MaterialType(serializable);

        if (materialType.DissolvesInto != null)
            throw new AssertionException(
                "MaterialType now carries DissolvesInto from JSON, so the game has been " +
                "fixed and this test should be inverted. Got " +
                string.Join(", ", materialType.DissolvesInto));
    }

    /// <summary>
    /// Dissolving overflows a stack buffer, so a material that declares DissolvesInto
    /// crashes the simulation as soon as sulfuric acid touches it.
    ///
    /// Utils.GetSelfAndAdjacentCoords always writes 9 coordinates, the 3x3 block including
    /// self. TryDissolve gives it stackalloc Vector2I[8] (BaseMaterial.cs:1715) and throws
    /// IndexOutOfRangeException on the ninth write. The sibling call site directly below,
    /// TryConductSpecificPair at :1830, allocates 9 and is correct.
    ///
    /// This has never been reachable in a shipped game: DissolvesInto cannot be set from
    /// data (see above) and no vanilla material declares it, so the two defects mask each
    /// other. Fixing the data binding alone would expose this crash.
    ///
    /// Set from code here, since data cannot reach it. BaseMaterial instances are shared
    /// for the whole world, so the probe material is restored before returning.
    ///
    /// Apatite is the target: static so it does not fall away from the acid, and it appears
    /// in no reaction, so nothing else can account for it disappearing.
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void DissolvingOverflowsItsCoordinateBuffer(Region r)
    {
        const string target = "Apatite";
        var material = Materials.TryGetBaseMaterial(target)
            ?? throw new AssertionException($"no BaseMaterial for '{target}'");

        var setter = typeof(BaseMaterial)
            .GetProperty(nameof(BaseMaterial.DissolvesInto))!
            .GetSetMethod(nonPublic: true)!;
        var original = material.DissolvesInto;

        try
        {
            setter.Invoke(material, [new[] { "Water".ToMaterialTypeId(), "Water".ToMaterialTypeId() }]);

            r.Set(10, 10, target);
            r.Set(10, 9, "Sulfuric Acid");

            try
            {
                r.Ticks(5);
            }
            catch (IndexOutOfRangeException)
            {
                return;   // the documented crash, thrown out of Simulation.Step
            }

            throw new AssertionException(
                "dissolving no longer overflows its buffer, so the game has been fixed and " +
                "this test should be inverted to assert that " + target + " dissolves into " +
                $"Water. Currently at (10,10): {r.At(10, 10) ?? "air"}\n{r.Dump()}");
        }
        finally
        {
            setter.Invoke(material, [original]);
        }
    }
}
