using System.Reflection;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// The version check has to do more than throw.
///
/// The mod loader loads a mod's assembly before calling Initialize, and the harness discovers
/// tests by scanning loaded assemblies, so a refused mod's tests were found and run anyway,
/// against a harness its Initialize never registered anything with. Measured before this was
/// fixed: a mod pinned to 0.2 was refused by 0.3.0-dev and all eight of its tests ran, and
/// passed.
///
/// The subject is TestHarness.Refused, a mod whose whole purpose is to ask for a version that
/// cannot exist. A real refused mod is the only honest way to test this: refusing the test mod
/// itself would take the suite down with it, and a hand-built refusal would not exercise the
/// part most likely to break, which is identifying the caller from the stack.
/// </summary>
public static class RefusalTests
{
    private const string FixtureAssembly = "TestHarness.Refused";

    [GameTest]
    public static void TheRefusalFixtureWasRefused()
    {
        var reason = Harness.RefusalReason(FixtureAssembly);

        if (reason == null)
            throw new AssertionException(
                $"'{FixtureAssembly}' should have been refused, since it asks for a harness " +
                "version that cannot exist. Either the fixture is not installed, or " +
                "RequireVersion no longer records which assembly called it, in which case a " +
                "mismatched mod's tests would run against an incompatible harness.");

        foreach (var expected in new[] { "built against", Harness.Version })
            if (!reason.Contains(expected))
                throw new AssertionException(
                    $"the refusal should name '{expected}' so the reader can see both versions. " +
                    $"Got: {reason}");
    }

    /// <summary>
    /// A mod that was not refused has no reason recorded. Without this, the test above would
    /// pass against an implementation that refused everything.
    /// </summary>
    [GameTest]
    public static void AModThatWasNotRefusedHasNoReason()
    {
        foreach (var name in new[] { "TestHarness", "TestHarness.Test" })
            if (Harness.RefusalReason(name) is string reason)
                throw new AssertionException($"'{name}' should not be refused, but: {reason}");

        if (Harness.RefusalReason("NoSuchAssembly") != null)
            throw new AssertionException("an unknown assembly should have no refusal recorded");
    }

    /// <summary>
    /// The lookup the executor uses to decide, driven the way the executor drives it. This is
    /// what connects "a refusal was recorded" to "this test will be failed", since a TestCase
    /// reports the assembly its method came from.
    /// </summary>
    [GameTest]
    public static void ATestCaseFromARefusedModResolvesToTheRefusal()
    {
        var fixtureMethod = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == FixtureAssembly)
            ?.GetType("Atomcraft.TestHarness.Refused.ModEntry")
            ?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);

        if (fixtureMethod == null)
            throw new AssertionException(
                $"could not find the fixture's Initialize; is '{FixtureAssembly}' installed?");

        var pretend = new TestCase { Method = fixtureMethod, Attr = new GameTestAttribute() };

        if (pretend.AssemblyName != FixtureAssembly)
            throw new AssertionException(
                $"a TestCase reported assembly '{pretend.AssemblyName}', expected '{FixtureAssembly}'. " +
                "The executor looks refusals up by this name, so it would fail to match.");

        if (Harness.RefusalReason(pretend.AssemblyName) == null)
            throw new AssertionException(
                "a test from the refused mod did not resolve to a refusal, so the executor " +
                "would run it");
    }
}
