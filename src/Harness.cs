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
    /// <summary>
    /// The running harness's version.
    ///
    /// Deliberately not a const. A const is inlined into whatever compiles against it, so a mod
    /// built against one harness and run against another would read the version it was built
    /// with and have no way to notice. That is the exact confusion RequireVersion exists to
    /// prevent, and it would be the harness handing it out.
    /// </summary>
    public static readonly string Version = ModEntry.Version;

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

        // Recorded before throwing, because throwing is not enough on its own. The loader
        // loads a mod's assembly before calling Initialize, and the harness discovers tests
        // by scanning loaded assemblies, so a refused mod's tests are found and run anyway,
        // against a harness its Initialize never got to register anything with. Refusing the
        // mod has to refuse its tests too or the check achieves nothing.
        var assembly = new System.Diagnostics.StackTrace().GetFrames()
            ?.Select(f => f.GetMethod()?.DeclaringType?.Assembly)
            .FirstOrDefault(a => a != null && a != typeof(Harness).Assembly);

        if (assembly != null)
            Refused[assembly.GetName().Name ?? "?"] = message;
        else
            Log.Warn("could not identify the mod that failed RequireVersion; its tests will run");

        Log.Error(message);
        throw new AssertionException(message);
    }

    private static readonly Dictionary<string, string> Refused = new();

    /// <summary>
    /// Why the named assembly's mod was refused, or null if it was not.
    ///
    /// Its tests are failed rather than skipped. A skipped test leaves the run green and the
    /// exit code zero, so a mod that was never tested at all would report success, which is
    /// the failure this whole mechanism exists to make impossible.
    /// </summary>
    public static string? RefusalReason(string? assemblyName) =>
        assemblyName != null && Refused.TryGetValue(assemblyName, out var why) ? why : null;

    private static bool Matches(string expected, string actual)
    {
        var want = expected.Split('.');
        var have = actual.Split('.');
        if (want.Length < 2 || have.Length < 2)
            return expected == actual;
        return want[0] == have[0] && want[1] == have[1];
    }
}
