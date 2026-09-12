using System.Collections.Generic;
using System.Threading.Tasks;
using Atomcraft;
using Godot;
using HarmonyLib;

namespace Atomcraft.TestHarness;

/// <summary>
/// Synthetic worlds, in place of the game's procedural generation.
///
/// Real worldgen takes seconds and produces terrain no test asked for. A fixture world is
/// flat, empty, and identical every run, which is what a test of a mod's behavior actually
/// wants: the mod's structures, and nothing else that could explain the result.
///
/// Installed by prefixing FileManager.GetWorldGenerationDelegate, which is where the game
/// chooses a generator by planet name, so everything downstream, saving, loading, spawning,
/// proceeds exactly as it does for a real world.
/// </summary>
public static class WorldFixtures
{
    /// <summary>The fixture to use for the next world creation, or null for real worldgen.</summary>
    public static string? Active { get; set; }

    /// <summary>Ground level in the fixture worlds. Below the workshop line, like Altitude.Deep.</summary>
    public const int GroundY = 5000;

    private static readonly Dictionary<string, Action<short[], short[], int, int>> Builders = new()
    {
        // Nothing at all: the cheapest possible world, for tests that build everything.
        ["empty"] = (_, _, _, _) => { },

        // A floor of bedrock-like stone at GroundY, so things have somewhere to land.
        ["flat"] = (materials, _, width, _) =>
        {
            var granite = Materials.GetBaseMaterialId("Granite");
            if (granite == -1)
                return;
            for (var x = 0; x < width; x++)
                materials[x + GroundY * width] = granite;
        },
    };

    public static IEnumerable<string> Names => Builders.Keys;

    /// <summary>
    /// Skips the game's automatic saves. Leaving a session writes the entire world, which
    /// is about ten seconds a test never asked for; Session.Save covers the cases that
    /// genuinely need it.
    /// </summary>
    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SaveGame))]
    internal static class SuppressAutomaticSaves
    {
        private static bool Prefix() => Session.AllowWrite("FileManager.SaveGame");
    }

    /// <summary>
    /// And the one that SaveGame does not cover: creating a world writes it straight back out.
    ///
    /// LoadOrCreatePlanetAsync generates the field and then calls SavePlanet with
    /// forceSaveAllSegments, so a fresh Enter pays for all 1536 segments before the test has done
    /// anything. Measured at 2.5 seconds of a 3.3 second fresh entry. It reaches SavePlanet
    /// directly rather than through SaveGame, so the patch above never sees it.
    ///
    /// A test that wants the world on disk calls Session.Save, which sets SaveRequested and so
    /// passes this predicate too. A test that reloads without ever saving would previously have
    /// found the generated world waiting for it; Session.Reload now refuses that case with an
    /// explanation rather than loading a planet with no segments in it.
    /// </summary>
    internal static void SuppressCreationSave(Harmony harmony)
    {
        var target = AccessTools.Method(typeof(FileManager), "SavePlanet");
        if (target == null)
        {
            Log.Warn("FileManager.SavePlanet not found; world creation will save all segments");
            return;
        }
        harmony.Patch(target, prefix: new HarmonyMethod(
            typeof(WorldFixtures), nameof(SkipUnlessAsked)));
    }

    private static bool SkipUnlessAsked() => Session.AllowWrite("FileManager.SavePlanet");

    /// <summary>
    /// The backstop, on the one method every write funnels through.
    ///
    /// SaveGame and SavePlanet are gated above because stopping there also skips serializing
    /// 1536 segments, which is where the time goes. This gate saves no work; it exists because
    /// enumerating entry points is how the previous gap happened. SavePlanet reached disk
    /// without passing through SaveGame and nobody noticed until a fresh world was found to
    /// cost two and a half seconds. WriteUniverseToDisk is where SaveWorldHeader, SavePlanet,
    /// SaveMap, and FlushUniverseToDisk all end up, so a path nobody enumerated still stops
    /// here.
    ///
    /// It logs when it fires, so a write the harness did not expect is visible rather than
    /// merely absent.
    /// </summary>
    internal static void SuppressUnaskedWrites(Harmony harmony)
    {
        var target = AccessTools.Method(typeof(FileManager), "WriteUniverseToDisk");
        if (target == null)
        {
            Log.Warn("FileManager.WriteUniverseToDisk not found; unenumerated save paths will " +
                     "still reach disk");
            return;
        }
        harmony.Patch(target, prefix: new HarmonyMethod(
            typeof(WorldFixtures), nameof(RefuseUnaskedWrite)));
    }

    private static bool RefuseUnaskedWrite() => Session.AllowWrite("FileManager.WriteUniverseToDisk");

    public static void Install(Harmony harmony)
    {
        var target = AccessTools.Method(typeof(FileManager), "GetWorldGenerationDelegate");
        if (target == null)
        {
            Log.Warn("FileManager.GetWorldGenerationDelegate not found; world fixtures unavailable");
            return;
        }
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(WorldFixtures), nameof(Prefix)));
        SuppressCreationSave(harmony);
        SuppressUnaskedWrites(harmony);
        Log.Info($"world fixtures available: {string.Join(", ", Builders.Keys)}");
    }

    private static bool Prefix(ref WorldGenerationDelegate __result)
    {
        if (Active == null)
            return true;                       // no fixture requested; let the game generate

        var name = Active;
        __result = _ => Generate(name);
        return false;
    }

    private static Task<(SaveData_Map, short[], short[], Dictionary<string, Vector2I?>, List<SaveData_NPCState>)>
        Generate(string name)
    {
        if (!Builders.TryGetValue(name, out var build))
            throw new AssertionException($"no world fixture named '{name}'. Known: {string.Join(", ", Builders.Keys)}");

        var field = Simulation.CurrentState.Field;
        var width = field.Width;
        var height = field.Height;
        var cells = width * height;

        // Reuses the field's existing arrays rather than allocating another 150 MB pair.
        // The caller assigns whatever is returned straight back onto the same field, and
        // the game has already flagged itself as world-loading, which is what its setters
        // check for.
        var materials = field.MaterialTypeIdMap;
        var heat = field.HeatMap;
        for (var i = 0; i < cells; i++)
        {
            materials[i] = -1;
            heat[i] = 290;
        }

        build(materials, heat, width, height);

        var map = new SaveData_Map
        {
            SpaceshipPosition = new Vector2I(width / 2, GroundY - 8),
            RoomMatrix = new string[1, 1],
        };

        Log.Info($"generated fixture world '{name}'");
        return Task.FromResult((map, materials, heat,
            new Dictionary<string, Vector2I?>(), new List<SaveData_NPCState>()));
    }
}
