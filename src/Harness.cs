namespace Atomcraft.TestHarness;

/// <summary>
/// The harness's identity, for consumers to check against.
///
/// The mod loader's dependency mechanism carries no version constraints: a test mod declares
/// that it needs TestHarness, not which TestHarness. With an API that is deliberately
/// unstable through 0.x, a mismatch would otherwise surface as a MissingMethodException
/// from somewhere unrelated, at the moment a test runs.
/// </summary>
public static class Harness
{
    public const string Version = ModEntry.Version;

    /// <summary>
    /// Fails immediately unless the running harness matches the version a mod was built
    /// against. Call from your mod's Initialize.
    ///
    /// Through 0.x the check is on major and minor: 0.1 accepts any 0.1.x and rejects 0.2.0,
    /// because a minor bump is where breaking changes live before 1.0.
    /// </summary>
    public static void RequireVersion(string expected)
    {
        if (Matches(expected, Version))
            return;

        var message =
            $"this mod was built against TestHarness {expected}, but {Version} is installed. " +
            "The 0.x API changes between minor versions; update the mod or install the " +
            "matching harness release.";
        Log.Error(message);
        throw new AssertionException(message);
    }

    private static bool Matches(string expected, string actual)
    {
        var want = expected.Split('.');
        var have = actual.Split('.');
        if (want.Length < 2 || have.Length < 2)
            return expected == actual;
        return want[0] == have[0] && want[1] == have[1];
    }
}
