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
}
