namespace Atomcraft.TestHarness;

/// <summary>
/// Actions the executor runs once per frame while the current test runs, cleared when it
/// ends however it ends.
///
/// A GUI test needs some conditions held continuously rather than established once: the
/// camera pinned against its own lerp, reopened windows re-closed. The test's coroutine
/// cannot do that, because it only runs on the frames it resumes; anything it needs done on
/// the frames it spends waiting has to live outside it. This is that place.
///
/// A hold that throws fails the test, exactly as if the test body threw: a hold is part of
/// the test's contract with the frame, and a hold that cannot do its job means every
/// assertion downstream is measuring an unheld world.
/// </summary>
internal static class FrameHolds
{
    private static readonly List<(string Name, Action Action)> Holds = new();

    /// <summary>Registers a hold, replacing any existing hold of the same name.</summary>
    public static void Set(string name, Action action)
    {
        Remove(name);
        Holds.Add((name, action));
    }

    public static void Remove(string name) => Holds.RemoveAll(h => h.Name == name);

    public static void Clear() => Holds.Clear();

    /// <summary>Runs every hold, in registration order.</summary>
    public static void Run()
    {
        // Over a copy: a hold may remove itself or register another.
        foreach (var (_, action) in Holds.ToArray())
            action();
    }
}
