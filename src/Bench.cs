using System.Diagnostics;

namespace Atomcraft.TestHarness;

/// <summary>
/// Marks a benchmark: a measurement rather than a verdict.
///
/// Not discovered unless a run asks for benchmarks with --atomtest-bench, because a
/// measurement that runs on every ordinary edit is noise, and because a body that cannot fail
/// should not be reported as something that passed. A mod that patches anything inside
/// Simulation.Step has to keep answering "what does this cost", and doing that through a test
/// that mostly logs makes every run slower and tells nobody anything.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GameBenchmarkAttribute : GameTestAttribute
{
}

/// <summary>
/// Timing a body, with the parts everyone gets wrong done once.
/// </summary>
public static class Bench
{
    /// <summary>
    /// Runs the body, discards that measurement, runs it again, and reports nanoseconds per
    /// operation.
    ///
    /// The discarded first pass is not politeness. Whichever variant is measured first in a
    /// process absorbs jit tiering and cache warmup, so comparing two variants without it
    /// reports the order they ran in as though it were a difference between them.
    ///
    /// Nothing here asserts. A timing threshold on a shared desktop is a flaky test, and the
    /// number worth having is the one in the record rather than a pass or a fail.
    /// </summary>
    public static BenchResult Measure(string name, int reps, Action body)
    {
        if (reps <= 0)
            throw new AssertionException($"benchmark '{name}' asked for {reps} reps");

        Run(reps, body);                       // discarded: warmup and tiering

        var elapsed = Run(reps, body);
        var result = new BenchResult(name, reps, elapsed);

        Log.Event("benchmark", new()
        {
            ["name"] = name,
            ["reps"] = reps,
            ["ms"] = (int)elapsed.TotalMilliseconds,
            ["nsPerOp"] = (int)result.NanosecondsPerOp,
        });

        Log.Info($"BENCH {name}: {result.NanosecondsPerOp:F1} ns/op over {reps} reps " +
                 $"({elapsed.TotalMilliseconds:F1} ms)");

        return result;
    }

    /// <summary>
    /// Two bodies measured the same way, reported together with the ratio.
    ///
    /// Each is warmed separately, so neither pays for the other's tiering: measuring A then B
    /// in one process without that makes B look faster whatever it does.
    /// </summary>
    public static void Compare(string name, int reps, string firstLabel, Action first,
        string secondLabel, Action second)
    {
        var a = Measure($"{name}.{firstLabel}", reps, first);
        var b = Measure($"{name}.{secondLabel}", reps, second);

        var ratio = a.NanosecondsPerOp == 0 ? 0 : b.NanosecondsPerOp / a.NanosecondsPerOp;

        Log.Event("benchmark_compare", new()
        {
            ["name"] = name,
            ["reps"] = reps,
            [firstLabel] = (int)a.NanosecondsPerOp,
            [secondLabel] = (int)b.NanosecondsPerOp,
            ["ratio"] = ratio.ToString("F3"),
        });

        Log.Info($"BENCH {name}: {secondLabel} is {ratio:F2}x {firstLabel} " +
                 $"({a.NanosecondsPerOp:F1} -> {b.NanosecondsPerOp:F1} ns/op)");
    }

    private static TimeSpan Run(int reps, Action body)
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < reps; i++)
            body();
        watch.Stop();
        return watch.Elapsed;
    }
}

/// <summary>What one measurement came to.</summary>
public sealed record BenchResult(string Name, int Reps, TimeSpan Elapsed)
{
    public double NanosecondsPerOp => Elapsed.TotalMilliseconds * 1_000_000.0 / Reps;
}
