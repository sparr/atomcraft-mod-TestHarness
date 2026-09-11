using Atomcraft;

namespace Atomcraft.TestHarness;

/// <summary>
/// One-shot investigations of the game itself, rather than tests of a mod.
/// Run with --atomtest-diagnose; they report and never fail a suite.
/// </summary>
public static class Diagnostics
{
    /// <summary>
    /// The ambient temperature profile by depth.
    ///
    /// Region.Clear leaves a flat 290 K because a known starting point makes tests
    /// reproducible, but that is not what the world looks like at depth, and any test whose
    /// subject is temperature-sensitive needs to know the difference.
    /// </summary>
    public static void AmbientProfile()
    {
        foreach (var y in new[] { 100, 500, 1000, 1728, 2000, 3000, 4000, 4800, 5000, 6000 })
        {
            Log.Event("diagnostic_ambient", new()
            {
                ["y"] = y,
                ["ambient"] = (int)y.GetAmbientTemperatureHeatmapValue(),
                ["weatherRange"] = y.IsWeatherRange(),
                ["aboveWorkshop"] = y.IsAboveWorkshop(),
            });
        }
    }

    /// <summary>
    /// Measures how far the two material id spaces disagree.
    ///
    /// Materials.GetMaterialTypeId reads MaterialTypesDict. Materials.GetBaseMaterialId,
    /// the short.ToMaterialName() extension, and every Step path read BaseMaterialsDict.
    /// SimField stores base material ids, so a mod using the other accessor places a
    /// different material than it asked for, silently.
    /// </summary>
    /// <summary>
    /// Validates every installed mod. Reports at every level including lint, since a survey is
    /// exactly the place to see the findings a normal check would suppress.
    ///
    /// Reads each mod's zip rather than the live registries, which is why this still works for
    /// a mod that loaded badly. A mod that cannot be installed at all, because it kills the
    /// game at load, can be inspected with Validation.InspectZip against its path directly.
    /// </summary>
    public static void ValidateInstalledMods()
    {
        Log.Banner("installed mod validation");

        foreach (var id in ModContent.InstalledModIds().OrderBy(s => s))
        {
            if (id is "0Harmony" or "GodotMonoModLoader") continue;
            Report(id, () => Validation.Inspect(id));
        }
    }

    private static void Report(string label, Func<IReadOnlyList<Finding>> inspect)
    {
        try
        {
            var findings = inspect();
            var errors = findings.Count(f => f.Severity == Severity.Error);
            Log.Info($"{label}: {findings.Count} finding(s), {errors} error(s)");
            foreach (var f in findings) Log.Info($"  {f}");
        }
        catch (Exception e)
        {
            Log.Error($"{label}: could not be inspected: {e.GetType().Name}: {e.Message}");
        }
    }

    public static void MaterialIdSpaces()
    {
        var total = 0;
        var diverged = 0;
        var firstDivergence = -1;
        var examples = new List<string>();

        for (short baseId = 0; baseId < Materials.Count; baseId++)
        {
            var name = baseId.ToMaterialName();
            if (string.IsNullOrEmpty(name))
                continue;

            total++;
            var typeId = Materials.GetMaterialTypeId(name);
            if (typeId == baseId)
                continue;

            diverged++;
            if (firstDivergence < 0)
                firstDivergence = baseId;

            if (examples.Count < 5)
            {
                // What a mod would actually get if it used the material type id.
                var wrong = typeId >= 0 ? typeId.ToMaterialName() ?? "<none>" : "<not found>";
                examples.Add($"'{name}' baseId={baseId} typeId={typeId} -> writing typeId places '{wrong}'");
            }
        }

        Log.Event("diagnostic_material_ids", new()
        {
            ["materials"] = total,
            ["diverged"] = diverged,
            ["agreed"] = total - diverged,
            ["firstDivergentBaseId"] = firstDivergence,
        });

        foreach (var example in examples)
            Log.Info($"id divergence: {example}");
    }
}
