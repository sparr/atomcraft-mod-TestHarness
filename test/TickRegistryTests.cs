namespace Atomcraft.TestHarness.Test;

/// <summary>
/// A mod's per-tick pass, driven by a region test.
///
/// The pass here is a stand-in for the shape a real mod has: work that hangs off
/// Simulation.Step in a game, which a region test never reaches because it calls
/// SimulateQuadrant directly.
/// </summary>
public static class TickRegistryTests
{
    private const string PassName = "harness.example.tick";

    private static int _calls;
    private static int _lastTick;
    private static RectInt _lastWindow;
    private static bool _registered;

    private static void EnsureRegistered()
    {
        if (_registered)
            return;
        _registered = true;

        TickRegistry.Register(new TickSpec
        {
            Name = PassName,
            Step = (window, tick) =>
            {
                _calls++;
                _lastTick = tick;
                _lastWindow = window;
            },
        });
    }

    [GameTest]
    public static void ARegisteredPassRunsOncePerTick(Region r)
    {
        EnsureRegistered();
        _calls = 0;

        r.Ticks(5);

        if (_calls != 5)
            throw new AssertionException($"expected 5 calls, got {_calls}");
    }

    /// <summary>
    /// The pass is told which tick it is on and which rectangle it may touch. Both matter:
    /// a pass that ignores the window acts world-wide inside a bounded test, and pixels it
    /// pushes past the edge wait in the margin for the next test to use that band.
    /// </summary>
    [GameTest(ChunksWide = 2)]
    public static void APassIsGivenTheTickAndTheRegionBounds(Region r)
    {
        EnsureRegistered();
        r.Tick = 40;

        r.Ticks(3);

        if (_lastTick != 42)
            throw new AssertionException($"expected the pass to see tick 42, saw {_lastTick}");

        if (_lastWindow.X != r.OriginX || _lastWindow.Y != r.OriginY)
            throw new AssertionException(
                $"window origin ({_lastWindow.X},{_lastWindow.Y}) is not the region's " +
                $"({r.OriginX},{r.OriginY})");
        if (_lastWindow.width != r.Width || _lastWindow.height != r.Height)
            throw new AssertionException(
                $"window size {_lastWindow.width}x{_lastWindow.height} is not the region's " +
                $"{r.Width}x{r.Height}");
    }

    /// <summary>
    /// A pass that throws fails the test that drove it, naming the pass. A mod whose tick
    /// work is broken should not present as a mod whose behavior is wrong.
    /// </summary>
    [GameTest]
    public static void AThrowingPassFailsTheTestThatDroveIt(Region r)
    {
        const string name = "harness.example.throwing";
        TickRegistry.Register(new TickSpec
        {
            Name = name,
            Step = (_, _) => throw new InvalidOperationException("deliberate"),
        });

        // Removed whatever happens: a registered pass runs for every test that ticks after
        // this one, so leaving it behind fails the rest of the suite with this test's error.
        try
        {
            try
            {
                r.Ticks(1);
            }
            catch (AssertionException ex) when (ex.Message.Contains(name))
            {
                return;
            }
        }
        finally
        {
            TickRegistry.Unregister(name);
        }

        throw new AssertionException("a throwing tick pass did not fail the test");
    }

    /// <summary>
    /// A pass registered BeforeSimulation runs in the gap the game itself offers: after the
    /// updated-this-tick flags are cleared and before anything moves. That is what lets a mod
    /// claim a cell and have the vanilla passes leave it alone, so its own movement beats
    /// gravity rather than losing to it.
    ///
    /// Both phases run the same pass, lifting a grain one cell and marking the destination.
    /// Before the quadrant passes the mark holds and the grain stays up. After them, gravity
    /// has already run, so the lift is undone on the following tick and the grain ends lower.
    /// Asserting the two differ is the point; asserting an exact cell would be asserting
    /// gravity's timing.
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void PhaseDecidesWhetherAPassBeatsGravity(Region r)
    {
        var before = RunLift(r, TickPhase.BeforeSimulation);
        var after = RunLift(r, TickPhase.AfterSimulation);

        if (before == after)
            throw new AssertionException(
                $"both phases left the grain at y={before}, so the phase is not being honored. " +
                "A pass running before the quadrant passes can mark a cell updated and keep it " +
                "where it put it; one running after cannot.");

        if (before >= after)
            throw new AssertionException(
                $"expected the before-simulation pass to hold the grain higher (smaller y), " +
                $"got before={before} after={after}");
    }

    /// <summary>
    /// Registers a lift-one-cell pass in the given phase, runs a few ticks, and reports where
    /// the grain ended up. Unregisters in a finally: a pass left behind runs for every test
    /// that ticks afterwards.
    /// </summary>
    private static int RunLift(Region r, TickPhase phase)
    {
        const string name = "harness.example.lift";
        const int startX = 10;
        const int startY = 20;

        r.Clear();
        r.Set(startX, startY, "Sand");
        r.Tick = 0;

        TickRegistry.Register(new TickSpec
        {
            Name = name,
            When = phase,
            Step = (window, tick) =>
            {
                var field = Atomcraft.Simulation.CurrentState.Field;
                for (var y = window.Y; y < window.Y + window.height; y++)
                for (var x = window.X; x < window.X + window.width; x++)
                {
                    if (field.Get(x, y) != Atomcraft.Materials.SAND) continue;
                    if (y - 1 < window.Y) continue;
                    if (field.Get(x, y - 1) != -1) continue;

                    // Move up one, then claim the destination so the vanilla passes skip it.
                    field.Set(x, y - 1, Atomcraft.Materials.SAND);
                    field.Set(x, y, -1);
                    field.SetUpdatedWithinCurrentTick(field.Index(x, y - 1));
                    return;
                }
            },
        });

        try
        {
            r.Ticks(4);

            for (var y = 0; y < r.Height; y++)
            for (var x = 0; x < r.Width; x++)
                if (r.At(x, y) == "Sand")
                    return y;

            throw new AssertionException($"the grain vanished during the {phase} run\n{r.Dump()}");
        }
        finally
        {
            TickRegistry.Unregister(name);
        }
    }
}
