using System.Collections;
using System.Linq;
using Atomcraft;
using Godot;

namespace Atomcraft.TestHarness;

/// <summary>
/// Entering and leaving a world, for tests that need a real session.
///
/// Most tests do not: pixel behavior runs against the live field with no world loaded,
/// which is far cheaper. A session is needed for the things that only exist inside one,
/// notably saving and loading, inventories, and anything reading Game.SaveData_World.
/// </summary>
public static class Session
{
    /// <summary>World name used by the harness. Doubles as the worldgen seed.</summary>
    public const string WorldName = "TestHarnessFixture";

    public static bool Active => Game.SessionActive;

    /// <summary>
    /// Creates a fixture world and enters it. Yield this from a frame-driven test.
    ///
    /// The session start is asynchronous inside the game: it awaits process frames while
    /// loading the planet, so a test must give it frames rather than calling and asserting.
    /// </summary>
    public static IEnumerator Enter(bool fresh) => Enter("flat", WorldMode.Creative, fresh);

    public static IEnumerator Enter(string fixture = "flat", WorldMode mode = WorldMode.Creative,
        bool fresh = true)
    {
        if (Active)
            throw new AssertionException("already in a session; leave it before entering another");

        // A world saved by an earlier run would be loaded instead of generated, so the
        // fixture would silently apply only the first time and every later run would test
        // whatever the last one left behind. Reloading deliberately keeps it.
        if (fresh)
            DeleteFixtureWorld();
        WorldFixtures.Active = fixture;

        var world = new SaveData_World
        {
            Name = WorldName,
            PlanetTypeBaseName = "primora",
            Mode = mode,
            EnemySetting = WorldEnemySetting.None,
            PlanetsDiscovered = new List<string> { "primora" },
            Spaceship = new SaveData_Spaceship(),
        };

        // On a reload the saved header is the one to use: it carries the session time,
        // discovered planets, and player records the save wrote.
        if (!fresh)
        {
            var saved = FileManager.GetWorlds()?.FirstOrDefault(w => w.Name == WorldName);
            if (saved != null)
                world = saved;
            else
                Log.Warn($"no saved world '{WorldName}' to reload; generating a fresh one");
        }

        Log.Info($"entering fixture world '{fixture}' in {mode} mode (fresh: {fresh})");
        Game.LoadWorldHeaderData(world);
        Game.StartHostSession(null, null, isOnline: false);

        yield return Wait.Until(() => Game.SessionActive, "the session to become active");
        yield return Wait.Until(() => Simulation.CurrentState?.Field != null, "the simulation field");

        // The game simulates in real time once a session is live, so without this the world
        // evolves between a test's writes and its assertions, by an amount that depends on
        // how long a frame took. Tests advance time explicitly with WorldTicks instead.
        PauseSimulation();

        Log.Event("session", new()
        {
            ["phase"] = "entered",
            ["fixture"] = fixture,
            ["mode"] = mode.ToString(),
            ["tick"] = Simulation.CurrentState?.Tick ?? -1,
        });
    }

    /// <summary>
    /// Leaves immediately, without waiting for frames. Used for cleanup after a test that
    /// failed inside a session, where there is no coroutine left to drive.
    /// </summary>
    public static void LeaveNow()
    {
        if (!Active)
            return;
        Game.Instance.ExitToMainMenu();
        WorldFixtures.Active = null;
    }

    /// <summary>Removes any world the harness previously created, so fixtures are pristine.</summary>
    public static void DeleteFixtureWorld()
    {
        var worlds = ProjectSettings.GlobalizePath("user://Worlds");
        if (!System.IO.Directory.Exists(worlds))
            return;

        foreach (var path in System.IO.Directory.GetFileSystemEntries(worlds))
        {
            var leaf = System.IO.Path.GetFileName(path);
            if (!leaf.StartsWith(WorldName, StringComparison.Ordinal))
                continue;
            try
            {
                if (System.IO.Directory.Exists(path))
                    System.IO.Directory.Delete(path, recursive: true);
                else
                    System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"could not remove stale fixture world '{leaf}': {ex.Message}");
            }
        }
    }

    /// <summary>Leaves the session and returns to the menu.</summary>
    public static IEnumerator Leave()
    {
        if (!Active)
            yield break;

        Game.Instance.ExitToMainMenu();
        yield return Wait.Until(() => !Game.SessionActive, "the session to end");
        WorldFixtures.Active = null;
        Log.Event("session", new() { ["phase"] = "left" });
    }

    /// <summary>Planet segments are saved in 128x128 blocks.</summary>
    public const int SegmentSize = 128;

    /// <summary>
    /// Suppresses the game's automatic saves, so only saves a test asks for happen.
    ///
    /// Game.StopSession writes every one of the 6144x6144 world's 1536 segments on the way
    /// out, which costs about ten seconds. A test that leaves a world it does not care
    /// about pays that for nothing, and a save/reload test pays it three times.
    /// </summary>
    public static bool SuppressAutomaticSaves { get; set; } = true;

    /// <summary>True while a save the harness asked for is running.</summary>
    internal static bool SaveRequested { get; private set; }

    /// <summary>Skips a save unless the harness asked for it.</summary>
    internal static bool ShouldSave() => SaveRequested || !SuppressAutomaticSaves;

    /// <summary>
    /// Writes a pixel and marks its segment dirty, so an incremental save includes it.
    ///
    /// Writing straight to the field does not mark anything dirty, and the game only tracks
    /// dirtiness for changes it made itself. A test that writes directly and then saves
    /// incrementally would save nothing and read back the world as it was.
    /// </summary>
    public static void SetPixel(int x, int y, string material)
    {
        var id = Materials.GetBaseMaterialId(material);
        if (id == -1)
            throw new AssertionException($"no such material: '{material}'");
        Simulation.CurrentState.Field.Set(x, y, id);
        MarkDirty(x, y);
    }

    /// <summary>
    /// Stops the world evolving on its own. A session test controls time with WorldTicks;
    /// anything else makes results depend on frame timing.
    /// </summary>
    public static void PauseSimulation()
    {
        if (!Simulation.IsSimulationPaused)
            Simulation.ToggleSimulationPaused();
    }

    public static void ResumeSimulation()
    {
        if (Simulation.IsSimulationPaused)
            Simulation.ToggleSimulationPaused();
    }

    /// <summary>Marks the segment containing a cell for the next incremental save.</summary>
    public static void MarkDirty(int x, int y) =>
        Simulation.DirtyPlanetSegmentOrigins.Add(
            new Vector2I(x / SegmentSize * SegmentSize, y / SegmentSize * SegmentSize));

    /// <summary>
    /// Writes the world to disk, as the game does at dawn or on exit.
    ///
    /// Saves every segment by default, which is slower but correct. The incremental path
    /// saves only segments the game considers dirty, and the game only tracks dirtiness for
    /// changes it made itself: pixels written straight into the field are missed even after
    /// MarkDirty, and the reload then returns a world that was never saved. A persistence
    /// test that passes by reading back unsaved state is worse than a slow one.
    ///
    /// Incremental remains available for a test whose changes the game made, where it is
    /// both correct and much cheaper.
    /// </summary>
    public static void Save(bool incremental = false)
    {
        if (!Active)
            throw new AssertionException("not in a session");
        SaveRequested = true;
        try
        {
            FileManager.SaveGame(Simulation.CurrentState, forceSaveAllSegments: !incremental);
        }
        finally
        {
            SaveRequested = false;
        }
        Log.Event("session", new() { ["phase"] = "saved", ["incremental"] = incremental });
    }

    /// <summary>
    /// Saves, leaves, and re-enters the same world from disk.
    ///
    /// The round trip a mod's persisted state has to survive, and the cheapest way to catch
    /// the unstable-material-id hazard: modded ids are assigned by load order, so anything
    /// written as a raw id comes back as a different material when the set of installed
    /// mods changes.
    /// </summary>
    public static IEnumerator SaveAndReload(string fixture = "flat", WorldMode mode = WorldMode.Creative,
        bool incremental = false)
    {
        Save(incremental);
        yield return Leave();

        // Without this the re-entry never touches the save file. FileManager caches the
        // universe it last loaded and GetOrLoadUniverseForWorld returns it whenever the name
        // matches, so re-entering the same world in one process hands back the object already
        // in memory. TryLoadUniverseFile is never called, which is the method the mod loader
        // hangs OnUniverseLoad on, so a mod's load hook never runs and every assertion about
        // persistence passes on state that never left memory.
        ForgetCachedUniverse();

        var before = UniverseLoads;
        yield return Enter(fixture, mode, fresh: false);

        if (UniverseLoads == before)
            throw new AssertionException(
                "re-entering the world did not read the save file, so nothing here would be " +
                "testing persistence. FileManager's universe cache was cleared before the " +
                "re-entry, so this means the load path changed shape.");

        Log.Event("session", new() { ["phase"] = "reloaded", ["loads"] = UniverseLoads });
    }

    /// <summary>
    /// How many times the game has actually read a universe from disk this run.
    ///
    /// Exists so a save-and-reload can prove it reloaded. Nothing else distinguishes a round
    /// trip through the file from a round trip through a cached object, and the two look
    /// identical to every assertion a test could make about the result.
    /// </summary>
    public static int UniverseLoads { get; private set; }

    internal static void RecordUniverseLoad() => UniverseLoads++;

    /// <summary>
    /// Drops FileManager's in-memory universe cache, so the next entry reads from disk.
    ///
    /// Reflection because the field is private and the only vanilla code that clears it sits
    /// inside a delete-the-world flow.
    /// </summary>
    public static void ForgetCachedUniverse()
    {
        var field = typeof(FileManager).GetField("ActiveUniverse",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new AssertionException(
                "FileManager.ActiveUniverse is gone, so the universe cache cannot be cleared " +
                "and save-and-reload would silently stop testing persistence.");

        field.SetValue(null, null);
    }

    /// <summary>
    /// Checksums of the chunks covering a rectangle, using the game's own
    /// SimSnapshot.GetChunkChecksum, which sums material and heat together.
    ///
    /// The right choice for determinism: heat makes it strictly more sensitive, and heat is
    /// where order-dependence would surface first, since Heat.Conductance runs on a tick
    /// schedule and Heat.AverageToAmbient on a position-hashed one. The wrong choice for a
    /// save and reload, where heat is recomputed rather than restored and the comparison
    /// would always differ. See ChunkMaterialChecksums for that.
    /// </summary>
    public static Dictionary<(int X, int Y), int> ChunkChecksums(int x, int y, int width, int height)
    {
        const int chunk = 64;
        var state = Simulation.CurrentState;
        var result = new Dictionary<(int X, int Y), int>();
        for (var cy = y / chunk * chunk; cy < y + height; cy += chunk)
        for (var cx = x / chunk * chunk; cx < x + width; cx += chunk)
            result[(cx, cy)] = state.GetChunkChecksum(cx, cy);
        return result;
    }

    /// <summary>
    /// Checksums of the chunks covering a rectangle, material only.
    ///
    /// Deliberately not the game's own SimSnapshot.GetChunkChecksum, which sums material
    /// and heat together. Heat is not persisted: a saved segment holds material ids only,
    /// and the load path recomputes temperature from each material's DefaultTemperature or
    /// the planet's ambient at that depth. A checksum including heat therefore always
    /// differs across a save and reload, and would say nothing about whether the round trip
    /// preserved anything.
    ///
    /// Comparing these catches what checking only the cells you wrote cannot: a lost
    /// boundary cell, a segment written back at the wrong offset, a neighbor disturbed.
    /// </summary>
    public static Dictionary<(int X, int Y), int> ChunkMaterialChecksums(int x, int y, int width, int height)
    {
        const int chunk = 64;
        var field = Simulation.CurrentState.Field;
        var result = new Dictionary<(int X, int Y), int>();

        for (var cy = y / chunk * chunk; cy < y + height; cy += chunk)
        for (var cx = x / chunk * chunk; cx < x + width; cx += chunk)
        {
            var sum = 0;
            for (var dy = 0; dy < chunk; dy++)
            for (var dx = 0; dx < chunk; dx++)
                sum = sum * 257 + field.Get(cx + dx, cy + dy);
            result[(cx, cy)] = sum;
        }
        return result;
    }

    /// <summary>
    /// Advances the real simulation, rather than a region in isolation.
    ///
    /// This is the game's own DoSimTick, so it runs the parallel quadrant passes over
    /// active chunks. Region.Ticks deliberately does not, which is why a test that cares
    /// about the parallel path has to be a session test.
    /// </summary>
    public static void WorldTicks(int count)
    {
        if (!Active)
            throw new AssertionException("not in a session");

        var before = Simulation.CurrentState.Tick;
        for (var i = 0; i < count; i++)
            Simulation.DoSimTick(1f / 60f);

        // DoSimTick runs regardless of the pause flag, which is what makes explicit
        // stepping possible, but a silent no-op would make a test pass for the wrong reason.
        if (Simulation.CurrentState.Tick != before + count)
            throw new AssertionException(
                $"the world did not advance: expected tick {before + count}, got {Simulation.CurrentState.Tick}");
    }
}
