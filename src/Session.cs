using System.Collections;
using System.Diagnostics;
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

    /// <summary>
    /// The world currently entered, or the one last entered. Tests that switch worlds need
    /// this: a mod carrying state from one world into another is a real bug, and a save
    /// written under the wrong name is a confusing one.
    /// </summary>
    public static string CurrentWorldName { get; private set; } = WorldName;

    public static bool Active => Game.SessionActive;

    /// <summary>
    /// Creates a fixture world and enters it. Yield this from a frame-driven test.
    ///
    /// The session start is asynchronous inside the game: it awaits process frames while
    /// loading the planet, so a test must give it frames rather than calling and asserting.
    /// </summary>
    public static IEnumerator Enter(bool fresh) => Enter("flat", WorldMode.Creative, fresh);

    /// <summary>
    /// The signature as it stood before worldName was added. Kept as a real overload rather
    /// than deleted: an optional parameter is a compile-time convenience, so adding one is a
    /// binary breaking change, and a mod compiled against the older harness fails with
    /// MissingMethodException rather than anything that names the problem.
    /// </summary>
    public static IEnumerator Enter(string fixture, WorldMode mode, bool fresh) =>
        Enter(fixture, mode, fresh, worldName: null);

    public static IEnumerator Enter(string fixture = "flat", WorldMode mode = WorldMode.Creative,
        bool fresh = true, string? worldName = null)
    {
        if (Active)
            throw new AssertionException("already in a session; leave it before entering another");

        CurrentWorldName = worldName ?? WorldName;

        // A world saved by an earlier run would be loaded instead of generated, so the
        // fixture would silently apply only the first time and every later run would test
        // whatever the last one left behind. Reloading deliberately keeps it.
        if (fresh)
        {
            DeleteFixtureWorld();
            UniverseWrites = 0;

            // And drop the cached universe with it. FileManager keeps the last universe it
            // loaded in a static, and deleting the files on disk does not touch it, so a fresh
            // world generated after a test that saved would inherit that test's 1536 segments in
            // memory and flush every one of them back out as part of its own creation. Measured
            // at 2.5 seconds per fresh entry, against 90 ms once the cache is cleared. It is a
            // correctness point as much as a speed one: a world called fresh should not be
            // carrying the previous world's contents.
            ForgetCachedUniverse();
        }
        WorldFixtures.Active = fixture;

        var world = new SaveData_World
        {
            Name = CurrentWorldName,
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
            var saved = FileManager.GetWorlds()?.FirstOrDefault(w => w.Name == CurrentWorldName);
            if (saved != null)
                world = saved;
            else
                Log.Warn($"no saved world '{CurrentWorldName}' to reload; generating a fresh one");
        }

        Log.Info($"entering fixture world '{fixture}' in {mode} mode (fresh: {fresh})");
        var watch = Stopwatch.StartNew();
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
            ["world"] = CurrentWorldName,
            ["fixture"] = fixture,
            ["mode"] = mode.ToString(),
            ["tick"] = Simulation.CurrentState?.Tick ?? -1,
            ["ms"] = watch.ElapsedMilliseconds,
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

        var watch = Stopwatch.StartNew();
        Game.Instance.ExitToMainMenu();
        yield return Wait.Until(() => !Game.SessionActive, "the session to end");
        WorldFixtures.Active = null;
        Log.Event("session", new() { ["phase"] = "left", ["ms"] = watch.ElapsedMilliseconds });
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

    /// <summary>
    /// How many universe writes the harness has let through since this world was generated.
    /// Counted at the chokepoint, so it records writes that happened rather than intentions.
    /// </summary>
    internal static int UniverseWrites { get; private set; }

    internal static void RecordUniverseWrite() => UniverseWrites++;

    /// <summary>
    /// Whether the current world has been written to disk since it was generated.
    ///
    /// Creating a world no longer saves it, so a reload is only meaningful after a Save, and
    /// without this the failure is a world that loads with no planet segments, looking like a
    /// bug in whatever the test was actually checking.
    ///
    /// Derived rather than tracked, because a flag set by hand goes stale in both directions
    /// and this one gates an exception. Setting it in Save would miss a save that was refused;
    /// setting it when AllowSaves is disposed would claim a world was written by a scope that
    /// wrapped nothing at all. Deleting the world outside Enter would leave either of those
    /// claiming a file that is gone.
    ///
    /// A write must have happened and the file must still be there. Both halves are cheap and
    /// neither can drift from the truth.
    /// </summary>
    internal static bool PlanetWritten =>
        UniverseWrites > 0 && System.IO.File.Exists(UniversePath);

    /// <summary>
    /// Where the current world's save file lives. Public because a test asserting on
    /// persistence sometimes needs to look at the file itself, and reconstructing this path by
    /// hand means duplicating the world name and the layout.
    /// </summary>
    public static string UniversePath =>
        ProjectSettings.GlobalizePath($"user://Worlds/{CurrentWorldName}.universe");

    /// <summary>Skips a save unless the harness asked for it.</summary>
    internal static bool ShouldSave() => SaveRequested || !SuppressAutomaticSaves;

    /// <summary>
    /// The mod that last had a save refused, or null if none has. Exposed so this is testable
    /// without scraping the log.
    /// </summary>
    public static string? LastUnaskedSaveCaller { get; private set; }

    /// <summary>
    /// Decides whether a save may proceed, and says something useful when it may not.
    ///
    /// A refused save is usually the game's own, at dawn or on the way out of a session, and
    /// that is unremarkable. It is worth a warning when the caller is a mod, because then
    /// somebody wrote a save call and is about to wonder why nothing was written.
    ///
    /// Naming the caller is a guess from the stack, which is fine for a message and would not
    /// be fine for the decision itself: the mod loader patches the save path too, so its frames
    /// can appear in a stack that has no test in it. A wrong guess here costs a misleading
    /// sentence, not a save that silently did or did not happen.
    /// </summary>
    internal static bool AllowWrite(string what)
    {
        if (ShouldSave())
            return true;

        var caller = ForeignCaller();
        if (caller != null)
        {
            LastUnaskedSaveCaller = caller;
            Log.Warn(
                $"{what} was called from {caller}, which is not the game, and the harness " +
                "suppressed it. Automatic saves are off so a test does not pay for writing a " +
                "world it does not care about. If this save was deliberate, call Session.Save, " +
                "or wrap the call in Session.AllowSaves() to let it through.");
        }
        else
        {
            Log.Info($"suppressed {what} (the game's own save)");
        }

        return false;
    }

    /// <summary>
    /// The first assembly on the stack that is neither the game, nor the harness, nor the
    /// machinery that sits between them. That is a mod or a test mod, and so a human who meant
    /// something by it.
    /// </summary>
    private static string? ForeignCaller()
    {
        var ours = new[] { "Atomcraft", "GodotSharp", "0Harmony", "GodotMonoModLoader" };

        foreach (var frame in new StackTrace().GetFrames() ?? Array.Empty<StackFrame>())
        {
            var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
            var name = assembly?.GetName().Name;

            if (name == null || assembly == typeof(Session).Assembly) continue;
            if (ours.Contains(name)) continue;
            if (name.StartsWith("System") || name.StartsWith("Microsoft") || name == "mscorlib") continue;

            return name;
        }

        return null;
    }

    /// <summary>
    /// Lets saves through for as long as the returned scope lives, for a test that calls the
    /// game's own save API rather than Session.Save.
    ///
    /// Suppression cannot tell a test calling FileManager.SaveGame from the game autosaving at
    /// dawn, because both arrive as the same call. Rather than guess from the call stack, which
    /// would be wrong in whichever direction is least obvious, a test says so:
    ///
    ///     using (Session.AllowSaves())
    ///         FileManager.SaveGame(Simulation.CurrentState, forceSaveAllSegments: true);
    ///
    /// Note this bypasses the first-save-complete rule in Save, so pass
    /// forceSaveAllSegments: true on a world that has not been saved before.
    /// </summary>
    public static IDisposable AllowSaves()
    {
        var previous = SaveRequested;
        SaveRequested = true;
        return new SaveScope(previous);
    }

    private sealed class SaveScope : IDisposable
    {
        private readonly bool _previous;
        public SaveScope(bool previous) => _previous = previous;
        public void Dispose() => SaveRequested = _previous;
    }

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

        // The first save of a generated world is always complete, whatever was asked for.
        // Creating a world no longer serializes its segments, so nothing else has ever put
        // them in the universe; an incremental save writes only what
        // Simulation.DirtyPlanetSegmentOrigins holds, which after worldgen is just what this
        // test touched. The reload then returns those few segments and air everywhere else,
        // and the assertion that would notice is the one that passes: the test's own pixels
        // are exactly the ones that were saved.
        if (incremental && !PlanetWritten)
        {
            Log.Info("first save of this world: saving every segment rather than only the " +
                     "dirty ones, because worldgen never wrote them and an incremental save " +
                     "would leave the rest of the planet absent from the file");
            incremental = false;
        }

        SaveRequested = true;
        var watch = Stopwatch.StartNew();
        try
        {
            FileManager.SaveGame(Simulation.CurrentState, forceSaveAllSegments: !incremental);
        }
        finally
        {
            SaveRequested = false;
        }
        Log.Event("session", new()
        {
            ["phase"] = "saved",
            // What happened, not what was requested: the first save is forced complete.
            ["incremental"] = incremental,
            ["ms"] = watch.ElapsedMilliseconds,
        });
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
        yield return Reload(fixture, mode);
    }

    /// <summary>
    /// Leaves and re-enters the current world, reading it back from disk. The reload half of
    /// SaveAndReload, separated so there is exactly one place that knows about the cache.
    ///
    /// Use this rather than Leave plus Enter whenever a test needs the world to come back off
    /// disk, and particularly when it has written something between the save and the reload
    /// that the reload is supposed to discard.
    /// </summary>
    public static IEnumerator Reload(string fixture = "flat", WorldMode mode = WorldMode.Creative)
    {
        if (!PlanetWritten)
            throw new AssertionException(
                "this world has never been saved, so there is nothing on disk to reload. " +
                "Generating a world no longer writes it out, because doing so cost every session " +
                "test about two and a half seconds it did not ask for. Call Session.Save first, " +
                "or use Session.SaveAndReload. Session.AllowSaves covers a test that saves " +
                "through the game's own API rather than through Session.Save.");

        yield return Leave();

        // Without this the re-entry never touches the save file. FileManager caches the
        // universe it last loaded and GetOrLoadUniverseForWorld returns it whenever the name
        // matches, so re-entering the same world in one process hands back the object already
        // in memory. TryLoadUniverseFile is never called, which is the method the mod loader
        // hangs OnUniverseLoad on, so a mod's load hook never runs and every assertion about
        // persistence passes on state that never left memory.
        ForgetCachedUniverse();

        var before = UniverseLoads;
        yield return Enter(fixture, mode, fresh: false, worldName: CurrentWorldName);

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
