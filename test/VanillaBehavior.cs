namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Calibration against vanilla behavior.
///
/// These are not tests of the game. They are tests of the harness: if it cannot reproduce
/// what vanilla obviously does, it cannot be trusted to judge what a mod does. Everything
/// asserted here is behavior any player would recognize.
/// </summary>
public static class VanillaBehavior
{
    [GameTest]
    public static void AirStaysAir(Region r)
    {
        r.Ticks(10);
        if (r.CountAir() != r.Width * r.Height)
            throw new AssertionException($"empty region did not stay empty\n{r.Dump()}");
    }

    [GameTest]
    public static void SandFallsToTheFloor(Region r)
    {
        const int floorY = 11;
        r.Fill(0, floorY, r.Width, 1, "Granite");
        r.Set(4, 0, "Sand");

        r.TicksUntil(() => r.At(4, floorY - 1) == "Sand", 200, "sand comes to rest on the floor");
        r.AssertCount("Sand", 1);
    }

    /// <summary>
    /// Static pixels (MaterialState.Static) are held in place by the simulation. Solid,
    /// liquid, and gas pixels all move under physics, so "it is hard" is not the same
    /// property as "it stays put".
    /// </summary>
    [GameTest]
    public static void StaticPixelsDoNotMove(Region r)
    {
        r.Set(4, 4, "Granite");
        r.Ticks(20);
        r.AssertAt(4, 4, "Granite");
        r.AssertCount("Granite", 1);
    }

    [GameTest]
    public static void UnsupportedSolidsFall(Region r)
    {
        r.Set(4, 2, "Compost");
        var startedAt = r.At(4, 2);
        r.Ticks(20);

        if (r.At(4, 2) == startedAt)
            throw new AssertionException($"an unsupported solid stayed where it was\n{r.Dump()}");
        r.AssertCount("Compost", 1);
    }

    [GameTest]
    public static void WaterSpreadsSideways(Region r)
    {
        // The floor spans the full region width: a partial floor lets liquid run off the
        // end and out of the region, which counts as lost rather than spread.
        const int floorY = 20;
        r.Fill(0, floorY, r.Width, 1, "Granite");
        r.Fill(30, 10, 4, 1, "Water");

        r.TicksUntil(() => CountInRow(r, floorY - 1, "Water") >= 4, 300,
            "all four water pixels reach the floor and spread out");
        r.AssertCount("Water", 4);
    }

    [GameTest]
    public static void HeatConductsIntoNeighbors(Region r)
    {
        r.Fill(0, 0, 8, 8, "Iron");
        r.SetHeat(4, 4, 2000);

        var before = r.HeatAt(4, 5);
        r.Ticks(50);
        var after = r.HeatAt(4, 5);

        if (after <= before)
            throw new AssertionException(
                $"heat did not conduct: neighbor went {before} -> {after}\n{r.Dump()}");
    }

    /// <summary>
    /// A sealed box, because a gas in an open region simply leaves. This is what the Wall
    /// parameter exists for: without it every buoyancy test builds its own container.
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void GasRises(Region r)
    {
        var floor = r.Height - r.WallThickness - 1;
        r.Set(32, floor, "Hydrogen Gas");

        r.Ticks(120);

        r.AssertCount("Hydrogen Gas", 1);

        var y = FindY(r, "Hydrogen Gas");
        if (y >= floor)
            throw new AssertionException(
                $"gas did not rise: started at y={floor}, ended at y={y}\n{r.Dump()}");
    }

    /// <summary>
    /// Falling solids tumble down slopes rather than stacking into a column. A staircase is
    /// the smallest shape that shows it. The region is walled so a grain that slides off the
    /// end is contained rather than silently leaving.
    ///
    /// The peak sits against the left wall on purpose. An earlier version put it mid-region,
    /// where a grain landing on the peak has open air on both sides and the direction it
    /// takes is a coin flip decided by the RNG; vanilla happened to send it right, and the
    /// test asserted right. A mod that changes the rolls failed it, correctly, and reported
    /// as much. Backing the peak against the wall leaves exactly one way down, so the test
    /// is about slopes rather than about which way a tie breaks.
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void SolidsSlideDownASlope(Region r)
    {
        const int steps = 6;
        var topX = r.WallThickness;                 // against the wall: no leftward option
        var floorY = r.Height - r.WallThickness - 1;

        // Columns descending to the right: tallest at topX, one cell shorter each step.
        for (var i = 0; i < steps; i++)
        {
            var height = steps - i;
            r.Fill(topX + i, floorY - height + 1, 1, height, "Granite");
        }

        r.Set(topX, floorY - steps - 3, "Sand");
        r.TicksUntil(() => FindX(r, "Sand") > topX, 300, "sand travels down the slope");

        r.AssertCount("Sand", 1);
        var restX = FindX(r, "Sand");
        if (restX <= topX)
            throw new AssertionException(
                $"sand did not travel down the slope: dropped at x={topX}, rests at x={restX}\n{r.Dump()}");
    }

    /// <summary>
    /// Poured from a single point, grains spread sideways as they land instead of stacking
    /// one cell wide. The pile is wider at its base than at its peak.
    /// </summary>
    [GameTest]
    public static void PouredSolidsFormAPile(Region r)
    {
        const int floorY = 30;
        const int dropX = 32;
        r.Fill(0, floorY, r.Width, 1, "Granite");

        // Each grain must come to rest before the next is dropped, or they land on each
        // other in mid-air and no pile forms. "Settled" means nothing is left above the
        // few rows the pile can occupy, not merely that the drop point is clear.
        const int settledAbove = 8;
        for (var i = 0; i < 12; i++)
        {
            r.Set(dropX, 2, "Sand");
            r.TicksUntil(() => CountInRows(r, 0, floorY - settledAbove, "Sand") == 0, 300,
                $"grain {i + 1} settles onto the pile");
        }

        r.AssertCount("Sand", 12);

        var bottom = CountInRow(r, floorY - 1, "Sand");
        var above = CountInRow(r, floorY - 2, "Sand");
        if (bottom <= 1)
            throw new AssertionException(
                $"sand stacked in a column instead of spreading: {bottom} grain(s) on the floor\n{r.Dump()}");
        if (bottom < above)
            throw new AssertionException(
                $"pile is not wider at the base: {bottom} on the floor, {above} above it\n{r.Dump()}");
    }

    private static int FindY(Region r, string material)
    {
        for (var y = 0; y < r.Height; y++)
        for (var x = 0; x < r.Width; x++)
            if (r.At(x, y) == material)
                return y;
        return -1;
    }

    private static int FindX(Region r, string material)
    {
        for (var y = 0; y < r.Height; y++)
        for (var x = 0; x < r.Width; x++)
            if (r.At(x, y) == material)
                return x;
        return -1;
    }

    private static int CountInRows(Region r, int yFrom, int yTo, string material)
    {
        var n = 0;
        for (var y = yFrom; y < yTo; y++)
            n += CountInRow(r, y, material);
        return n;
    }

    private static int CountInRow(Region r, int y, string material)
    {
        var n = 0;
        for (var x = 0; x < r.Width; x++)
            if (r.At(x, y) == material)
                n++;
        return n;
    }

    /// <summary>
    /// A pixel can be spawned directly at a temperature, rather than placed cold and heated.
    /// Ramping a pixel up to a target passes it through every band on the way, which fires
    /// reactions the test was not asking about.
    /// </summary>
    [GameTest]
    public static void PixelsCanBeSpawnedAtATemperature(Region r)
    {
        r.Set(4, 4, "Iron", 1200);
        r.AssertAt(4, 4, "Iron");
        if (r.HeatAt(4, 4) != 1200)
            throw new AssertionException($"expected 1200 K at the spawn, got {r.HeatAt(4, 4)}");

        // The neighbor is untouched: setting a pixel's heat is not a local heat source.
        if (r.HeatAt(5, 4) != 290)
            throw new AssertionException($"spawning changed a neighbor to {r.HeatAt(5, 4)} K");

        r.Fill(8, 4, 3, 2, "Iron", 800);
        for (var y = 4; y < 6; y++)
        for (var x = 8; x < 11; x++)
            if (r.HeatAt(x, y) != 800)
                throw new AssertionException($"fill left ({x},{y}) at {r.HeatAt(x, y)} K, expected 800");
    }

    /// <summary>
    /// The harness exposes the game's own placement rule, so a test can spawn a pixel the
    /// way a player's placement would rather than at whatever the cell was left at.
    /// </summary>
    [GameTest(ChunksTall = 4)]
    public static void PlacementTemperatureVariesWithDepth(Region r)
    {
        // A tall region spans hundreds of cells of depth. Ambient is a function of altitude,
        // so asking about one row says nothing about another.
        var top = r.AmbientAt(0);
        var bottom = r.AmbientAt(r.Height - 1);
        Log.Info($"ambient across {r.Height} rows from y={r.OriginY}: top {top} K, bottom {bottom} K");

        if (r.PlacementTemperature("Sand", 0) != top)
            throw new AssertionException("placement temperature ignored the row it was given");
        if (r.PlacementTemperature("Sand", r.Height - 1) != bottom)
            throw new AssertionException("placement temperature ignored the row it was given");
    }

    [GameTest]
    public static void PlacementTemperatureFollowsTheGamesRule(Region r)
    {
        var iron = r.PlacementTemperature("Iron", 0);
        if (iron <= 0)
            throw new AssertionException($"implausible placement temperature for Iron: {iron}");

        r.Set(4, 4, "Iron", iron);
        if (r.HeatAt(4, 4) != iron)
            throw new AssertionException($"spawn did not honour the placement temperature");
    }

    /// <summary>
    /// ConveyInto fires the target's OnImpact, and the handler can move pixels even though
    /// TryConvey reports that nothing moved.
    ///
    /// Allow Liquids is the subject because its OnImpact discriminates: a liquid conveyed into
    /// it is passed through to the far side, a solid is refused. Measured, not assumed. Note
    /// the return is false in both cases, because the conveyor itself never moved the source
    /// pixel; the filter did.
    ///
    /// Nitroglycerin would be the obvious subject and is unusable here: igniting anything that
    /// explodes reads Avatars.LocalAvatar.PlayerId, which is null without a session, so every
    /// explosion path throws in a region test.
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void ConveyingALiquidIntoAllowLiquidsPassesItThrough(Region r)
    {
        r.Set(10, 10, "Allow Liquids");
        r.Set(9, 10, "Water");

        if (r.ConveyInto(9, 10, 10, 10))
            throw new AssertionException(
                "conveying into an occupied cell should report that the conveyor moved nothing");

        r.AssertAt(11, 10, "Water");      // through the filter
        r.AssertAt(9, 10, null);
        r.AssertAt(10, 10, "Allow Liquids");
    }

    /// <summary>
    /// The same impact, refused. Without this the test above would pass on any handler that
    /// moved everything, which would not be a filter at all.
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void ConveyingASolidIntoAllowLiquidsIsRefused(Region r)
    {
        r.Set(10, 10, "Allow Liquids");
        r.Set(9, 10, "Sand");

        r.ConveyInto(9, 10, 10, 10);

        r.AssertAt(9, 10, "Sand");        // stayed put
        r.AssertAt(11, 10, null);
    }

    /// <summary>
    /// The other half of the contract: conveying into air moves the pixel and fires no impact.
    /// Without this, ConveyInto could be doing nothing at all and the test above would still
    /// pass on the strength of the sand never moving.
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void ConveyingIntoAirMovesThePixel(Region r)
    {
        r.Set(9, 10, "Sand");

        if (!r.ConveyInto(9, 10, 10, 10))
            throw new AssertionException($"conveying into air should move the pixel\n{r.Dump()}");

        r.AssertAt(10, 10, "Sand");
        r.AssertAt(9, 10, null);
    }
}
