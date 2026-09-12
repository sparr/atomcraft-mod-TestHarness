namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Benchmarks, and the tests that keep the benchmark machinery honest.
///
/// The tests here are ordinary tests. The benchmark below is excluded from a normal run and
/// only appears under --atomtest-bench, which is the point: a measurement should not run on
/// every edit, and a body that cannot fail should not be counted as something that passed.
/// </summary>
public static class BenchTests
{
    [GameTest]
    public static void MeasureReportsPerOperationTime()
    {
        var result = Bench.Measure("harness.selftest.empty", 1000, () => { });

        if (result.Reps != 1000)
            throw new AssertionException($"reported {result.Reps} reps, expected 1000");

        if (result.NanosecondsPerOp < 0)
            throw new AssertionException("negative time per operation");
    }

    /// <summary>
    /// The body runs twice, once discarded. A measurement that skipped the warmup would report
    /// whichever variant ran first as slower, which is the mistake this exists to prevent.
    /// </summary>
    [GameTest]
    public static void MeasureRunsTheBodyTwice()
    {
        var calls = 0;
        Bench.Measure("harness.selftest.counted", 10, () => calls++);

        if (calls != 20)
            throw new AssertionException(
                $"expected 20 calls for 10 reps warmed and measured, got {calls}");
    }

    [GameTest]
    public static void ZeroRepsIsRefused()
    {
        try
        {
            Bench.Measure("harness.selftest.zero", 0, () => { });
            throw new AssertionException("a benchmark with no reps should have been refused");
        }
        catch (AssertionException e) when (e.Message.Contains("0 reps"))
        {
            // expected
        }
    }

    /// <summary>
    /// Benchmarks are not part of an ordinary run. If this ever stops holding, every suite
    /// picks up someone else's timing loops.
    /// </summary>
    [GameTest]
    public static void BenchmarksAreNotDiscoveredAsTests()
    {
        var tests = TestDiscovery.Discover(null, benchmarks: false);
        var benches = TestDiscovery.Discover(null, benchmarks: true);

        if (tests.Any(t => t.IsBenchmark))
            throw new AssertionException("a benchmark was discovered as a test");

        if (benches.Count == 0)
            throw new AssertionException(
                "no benchmarks were discovered at all, so this test cannot show they are " +
                "separated rather than absent");

        if (benches.Any(b => !b.IsBenchmark))
            throw new AssertionException("a test was discovered as a benchmark");
    }

    /// <summary>A benchmark, so the separation above has something real to separate.</summary>
    [GameBenchmark]
    public static void MaterialLookupByName()
    {
        Bench.Compare("materials.lookup", 20_000,
            "byName", () => Atomcraft.Materials.GetBaseMaterialId("Water"),
            "byId", () => ((short)1).ToMaterialName());
    }
}
