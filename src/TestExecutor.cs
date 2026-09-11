using System.Diagnostics;
using Atomcraft;
using Godot;

namespace Atomcraft.TestHarness;

/// <summary>
/// Runs discovered tests, one at a time, with the world pinned to a known state.
///
/// Determinism is the harness's job, not each test's. Before every test the weather is
/// pinned, the nondeterministic RNG is seeded, the tick is set, and the region is cleared,
/// so a test that passes once passes every time.
/// </summary>
public static class TestExecutor
{
    /// <summary>
    /// Everything the game needs before a single cell can be simulated or queried. Public
    /// and idempotent because diagnostics run before the suite and need it too: asking for
    /// an ambient temperature goes through Game.CurrentPlanetType, which is null until a
    /// world header exists.
    /// </summary>
    public static void EnsureGameReady()
    {
        EnsureRngInitialized();
        EnsureFieldReady();
        EnsureWorldHeader();
        EnsureLocalProfile();
        FieldRegistry.RegisterVanilla();
    }

    private static List<TestCase>? _queue;
    private static int _index;
    private static int _passed, _failed, _skipped;
    // A stack, not a single enumerator: a test yields another coroutine to compose steps
    // ("enter a world", "save and reload"), and the inner one has to be driven to
    // completion before the outer resumes.
    private static readonly Stack<System.Collections.IEnumerator> _running = new();
    private static Wait? _waiting;
    private static int _framesOnCurrent;
    private static TestCase? _current;

    /// <summary>
    /// The test currently running, or null between tests. Exists so an artifact can be filed
    /// under the test that produced it without every caller passing its own name.
    /// </summary>
    public static string? CurrentTestName => _current?.Name;
    private static Region? _currentRegion;
    private static Stopwatch? _watch;
    private static int _exceptionsBefore;

    /// <summary>Starts the suite. Call <see cref="Step"/> once per frame until it returns true.</summary>
    public static void Begin(string? filter)
    {
        EnsureGameReady();

        var all = TestDiscovery.Discover(null);
        _queue = all.Where(t => t.Matches(filter)).ToList();
        _index = 0;
        _passed = _failed = _skipped = 0;
        _selectionError = SelectionError(filter, all.Count, _queue.Count);

        // Before any test runs, so nothing left by the previous run can be mistaken for
        // evidence from this one.
        Artifacts.Reset();

        Log.Event("run_start", new()
        {
            ["tests"] = _queue.Count,
            ["discovered"] = all.Count,
            ["filter"] = filter,
        });

        if (_selectionError != null)
            Log.Error(_selectionError);
    }

    private static string? _selectionError;

    /// <summary>
    /// Why a run selected no tests, or null if it selected some.
    ///
    /// Running nothing and reporting success is the worst outcome a test runner has: it is
    /// indistinguishable from everything passing, and CI goes green. A filter that matches
    /// nothing is far likelier to be a typo, or a regex written for a matcher that only does
    /// substrings, than a deliberate request to run no tests.
    /// </summary>
    public static string? SelectionError(string? filter, int discovered, int selected)
    {
        if (selected > 0)
            return null;

        if (discovered == 0)
            return "no tests were found at all. A test mod must be installed alongside the " +
                   "harness and must have loaded; check the log for a mod that failed to " +
                   "initialize.";

        if (filter == null)
            return $"no tests were selected out of {discovered} discovered, with no filter set.";

        return $"the filter '{filter}' matched none of the {discovered} discovered tests, so " +
               "this run tested nothing. The filter is a case-insensitive substring of the " +
               "full test name, not a regular expression: 'A|B' matches a test whose name " +
               "literally contains \"A|B\". Run without a filter to see the names.";
    }

    /// <summary>
    /// Advances the suite by one engine frame.
    ///
    /// A synchronous test runs to completion within a single call. A frame-driven test
    /// advances by one yield, so the game gets a frame between steps and can actually load
    /// a world, write a save, or run its own callbacks.
    /// </summary>
    /// <returns>True when every test has finished.</returns>
    public static bool Step()
    {
        if (_queue == null)
            throw new AssertionException("Step called before Begin");

        if (_running.Count > 0)
        {
            Advance();
            return false;
        }

        if (_index >= _queue.Count)
        {
            Log.Event("run_summary", new()
            {
                ["passed"] = _passed,
                ["failed"] = _failed,
                ["skipped"] = _skipped,
                ["artifacts"] = Artifacts.Count,
                ["selectionError"] = _selectionError,
            });
            return true;
        }

        var test = _queue[_index++];

        // A mod whose Initialize was refused for a version mismatch never registered its
        // channels, state, materials, or patches, so its tests would run against a harness
        // that knows nothing about it. Failed rather than skipped on purpose: a skip leaves
        // the run green and the exit code zero, and a mod that was never tested at all would
        // report success.
        if (Harness.RefusalReason(test.AssemblyName) is string refusal)
        {
            test.Status = "failed";
            test.Failure = refusal;
            _failed++;
            Log.Event("test", new()
            {
                ["name"] = test.Name,
                ["status"] = "failed",
                ["failure"] = refusal,
            });
            return false;
        }

        if (test.Attr.Skip is string reason)
        {
            test.Status = "skipped";
            _skipped++;
            Log.Event("test", new()
            {
                ["name"] = test.Name,
                ["status"] = "skipped",
                ["reason"] = reason,
            });
            return false;
        }

        Start(test);
        return false;
    }

    /// <summary>
    /// Suite exit code: 0 when nothing failed and something ran. A run that selected no tests
    /// fails, because a green run that measured nothing is worse than a red one.
    /// </summary>
    public static int ExitCode => _failed == 0 && _selectionError == null ? 0 : 1;

    private static void Start(TestCase test)
    {
        _current = test;
        _watch = Stopwatch.StartNew();
        _framesOnCurrent = 0;
        _exceptionsBefore = ExceptionSuppressor.TotalDistinct;
        ExceptionSuppressor.Context = test.Name;
        _currentRegion = null;

        try
        {
            Pin(test);

            var parameters = test.Method.GetParameters();
            object?[] args;
            if (parameters.Length == 0)
            {
                args = Array.Empty<object?>();
            }
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Region))
            {
                _currentRegion = Region.Allocate(test.Name, test.Attr.ChunksWide, test.Attr.ChunksTall,
                    test.Attr.Band, test.Attr.Wall, test.Attr.WallThickness);
                _currentRegion.Tick = test.Attr.StartTick;
                args = new object?[] { _currentRegion };
            }
            else
            {
                throw new AssertionException(
                    "unsupported signature: a test takes no parameters or a single Region");
            }

            var result = test.Method.Invoke(null, args);
            if (result is System.Collections.IEnumerator coroutine)
            {
                _running.Clear();
                _running.Push(coroutine);
                _waiting = null;
                Advance();
                return;
            }

            Finish("passed", null);
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            Finish("failed", Describe(ex.InnerException ?? ex));
        }
        catch (Exception ex)
        {
            Finish("failed", Describe(ex));
        }
    }

    /// <summary>Advances a frame-driven test by at most one yield per frame.</summary>
    private static void Advance()
    {
        _framesOnCurrent++;
        if (_current != null && _framesOnCurrent > _current.Attr.TimeoutFrames)
        {
            var what = _waiting?.Describe() ?? "the test";
            Finish("failed", $"still waiting for {what} after {_framesOnCurrent} frames");
            return;
        }

        try
        {
            if (_waiting != null)
            {
                _waiting.Tick();
                if (!_waiting.Done)
                    return;
                _waiting = null;
            }

            var top = _running.Peek();
            if (!top.MoveNext())
            {
                _running.Pop();
                if (_running.Count == 0)
                    Finish("passed", null);
                return;                        // the parent resumes on the next frame
            }

            switch (top.Current)
            {
                case Wait wait:
                    _waiting = wait;
                    break;
                case System.Collections.IEnumerator nested:
                    _running.Push(nested);
                    break;
                // Anything else, including a bare `yield return null`, is one frame.
            }
        }
        catch (Exception ex)
        {
            Finish("failed", Describe(ex));
        }
    }

    private static void Finish(string status, string? failure)
    {
        var test = _current!;
        _running.Clear();
        _waiting = null;
        ExceptionSuppressor.Context = "run";
        _watch?.Stop();
        test.ElapsedMs = _watch?.ElapsedMilliseconds ?? 0;
        test.Status = status;
        test.Failure = failure;

        // A test that passed while the engine was logging exceptions is not a passing test.
        var provoked = ExceptionSuppressor.TotalDistinct - _exceptionsBefore;
        if (provoked > 0 && test.Status == "passed")
        {
            test.Status = "failed";
            test.Failure = $"the engine logged {provoked} new exception signature(s) during this test";
        }

        // A test that failed partway through a session never reached its Leave, and the
        // next test would then fail with "already in a session" for reasons of its own.
        if (Session.Active)
        {
            try
            {
                Session.LeaveNow();
                Log.Info($"left a session {test.Name} had open");
            }
            catch (Exception ex)
            {
                Log.Warn($"could not leave the session after {test.Name}: {ex.Message}");
            }
        }

        // Hand the band space back. A region is cleared when it is handed out, so holding
        // the cursor past the end of a test only shrinks how many tests a run can hold.
        Region.ReleaseAll();

        if (test.Status == "passed") _passed++; else _failed++;

        Log.Event("test", new()
        {
            ["name"] = test.Name,
            ["status"] = test.Status,
            ["ms"] = (int)test.ElapsedMs,
            ["frames"] = _framesOnCurrent,
            ["failure"] = test.Failure,
        });

        if (test.Failure != null)
            Log.Info($"FAILED {test.Name}\n{test.Failure}");

        _current = null;
        _currentRegion = null;
    }

    /// <summary>
    /// A player profile, which the game normally gets from its profile selection menu.
    ///
    /// Without one, entering a world fails in a way that is almost impossible to diagnose:
    /// Client.JoinGame dereferences Game.LocalProfile.PlayerName, Session.Start is an
    /// unawaited async Task so the NullReferenceException is swallowed entirely, and the
    /// session simply never becomes active. No error, no stack, nothing in the log after
    /// "Sending join game message to server".
    /// </summary>
    private static void EnsureLocalProfile()
    {
        if (Game.LocalProfile != null)
            return;

        Game.LoadLocalProfile(new SaveData_Profile
        {
            PlayerName = "TestHarness",
            AvatarSettings = default,
            TutorialComplete = true,      // the tutorial path reads Data.TutorialGoals
        });
        Log.Info("synthesized a local profile (no profile menu is visited)");
    }

    /// <summary>Everything that would otherwise make a test flaky.</summary>
    private static void Pin(TestCase test)
    {
        // Per test, not once per run. Leaving a session tears down everything the game
        // needs before it will simulate: StopSession nulls Game.SaveData_World, and
        // Simulation.Reset installs a fresh SimSnapshot whose UpdatedWithinCurrentTick and
        // ElectricalChargeMap are unallocated again. Each check is cheap once satisfied.
        EnsureGameReady();

        Simulation.StartSunny();
        RNG.SetNonDeterministicSeed(12345);
        if (Simulation.CurrentState != null)
            Simulation.CurrentState.Tick = test.Attr.StartTick;

        // Channels can hold state outside the region a test is handed, and a world-level
        // aggregate would otherwise carry between tests silently. Registered global state is
        // worse: no rectangle describes it at all, so nothing else would ever clear it.
        if (test.Attr.ResetModState)
        {
            FieldRegistry.ResetAll();
            StateRegistry.ResetAll();
        }

        // World mode is global state, so it is set per test rather than once per run.
        if (Game.SaveData_World is SaveData_World world)
            world.Mode = test.Attr.Mode;
    }

    /// <summary>
    /// The RNG volume is normally generated when a session starts. Tests never start one,
    /// so the harness does it: most BaseMaterial.Step paths roll against it, and an
    /// uninitialized volume would fault rather than misbehave.
    /// </summary>
    private static void EnsureRngInitialized()
    {
        if (RNG.IsInitialized)
            return;
        Log.Info("initializing RNG volume (normally done at session start)");
        var watch = Stopwatch.StartNew();
        RNG.Init();
        Log.Info($"RNG ready in {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// SimField's constructor allocates only MaterialTypeIdMap and HeatMap. The
    /// UpdatedWithinCurrentTick and ElectricalChargeMap arrays stay null until a world
    /// loads, and every Step path reads the first of them, so a test would fault rather
    /// than fail. Allocating them is a one-time cost of clearing 37.7M entries; after this
    /// the harness clears only the cells a region owns.
    /// </summary>
    private static void EnsureFieldReady()
    {
        var field = Simulation.CurrentState?.Field;
        if (field == null)
            throw new AssertionException("Simulation.CurrentState.Field is null");

        var cells = field.Width * field.Height;
        if (field.UpdatedWithinCurrentTick == null || field.UpdatedWithinCurrentTick.Length != cells)
        {
            var watch = Stopwatch.StartNew();
            field.ResetUpdatedWithinCurrentTick();
            Log.Info($"allocated UpdatedWithinCurrentTick for {cells} cells in {watch.ElapsedMilliseconds} ms");
        }

        if (field.ElectricalChargeMap == null || field.ElectricalChargeMap.Length != cells)
        {
            // Assigning outside a world load logs a warning from the game, which is
            // expected here and harmless: nothing else is touching the field yet.
            Simulation.SetIsWorldLoading(isLoading: true);
            try
            {
                field.ElectricalChargeMap = new short[cells];
            }
            finally
            {
                Simulation.SetIsWorldLoading(isLoading: false);
            }
            Log.Info($"allocated ElectricalChargeMap for {cells} cells");
        }
    }

    /// <summary>
    /// Some per-cell code reaches past the field for planet data: SimulateCoords asks for
    /// the ambient temperature at a given altitude, which goes through
    /// Game.CurrentPlanetType and dereferences Game.SaveData_World. That is null until a
    /// world is loaded, so stepping a region would fault.
    ///
    /// A header is not a world. This synthesizes the smallest one that makes planet lookups
    /// resolve, without worldgen, disk, or a session.
    /// </summary>
    private static void EnsureWorldHeader()
    {
        if (Game.SaveData_World != null)
            return;

        const string planet = "primora";
        if (!PlanetTypes.GetByBaseName(planet, out _))
            throw new AssertionException($"planet type '{planet}' not found; PlanetTypes may have changed");

        Game.LoadWorldHeaderData(new SaveData_World
        {
            Name = "AtomcraftTestHarness",
            PlanetTypeBaseName = planet,
            Mode = WorldMode.Creative,   // overridden per test by Pin()
            EnemySetting = WorldEnemySetting.None,
            PlanetsDiscovered = new List<string> { planet },
            Spaceship = new SaveData_Spaceship(),
        });
        Log.Info($"synthesized a world header for planet '{planet}' (no world is loaded)");
    }

    private static string Describe(Exception ex) =>
        ex is AssertionException ? ex.Message : ex.ToString();
}
