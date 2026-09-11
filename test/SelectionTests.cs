namespace Atomcraft.TestHarness.Test;

/// <summary>
/// A run that selects no tests must fail.
///
/// Reported by the Pressure mod: --atomtest-filter is a substring match, so a filter written
/// as a regex, 'Registration|ResidueHunt', matched nothing, ran zero tests, and reported
/// success. Running nothing and exiting green is the worst outcome a runner has, because it is
/// indistinguishable from everything passing.
/// </summary>
public static class SelectionTests
{
    [GameTest]
    public static void AFilterMatchingNothingIsAnError()
    {
        var error = TestExecutor.SelectionError("Registration|ResidueHunt", discovered: 114, selected: 0);

        if (error == null)
            throw new AssertionException(
                "a filter that matched none of 114 tests should be an error, not a clean run");

        // The message has to say why, or the reader retries the same regex.
        foreach (var expected in new[] { "Registration|ResidueHunt", "substring", "not a regular expression" })
            if (!error.Contains(expected))
                throw new AssertionException(
                    $"the message should mention '{expected}'. Got: {error}");
    }

    [GameTest]
    public static void AFilterThatMatchesSomethingIsFine()
    {
        if (TestExecutor.SelectionError("Vanilla", discovered: 114, selected: 7) != null)
            throw new AssertionException("a filter that selected tests should not be an error");
    }

    /// <summary>
    /// No tests at all is a different problem with a different fix, so it gets its own message
    /// rather than blaming a filter that was never set.
    /// </summary>
    [GameTest]
    public static void DiscoveringNothingSaysSoDistinctly()
    {
        var error = TestExecutor.SelectionError(null, discovered: 0, selected: 0);

        if (error == null || !error.Contains("no tests were found at all"))
            throw new AssertionException($"expected a distinct message for an empty run. Got: {error}");

        if (error.Contains("filter"))
            throw new AssertionException(
                "an empty discovery should not blame a filter, which may not have been set");
    }

    /// <summary>
    /// The filter is a substring of the full, assembly-qualified name, which is the behavior
    /// the error message promises.
    /// </summary>
    [GameTest]
    public static void TheFilterIsACaseInsensitiveSubstringOfTheFullName()
    {
        var test = TestDiscovery.Discover(null)
            .FirstOrDefault(t => t.Name.EndsWith(nameof(TheFilterIsACaseInsensitiveSubstringOfTheFullName)));

        if (test == null)
            throw new AssertionException("could not find this test in discovery");

        if (!test.Matches("thefilterisacaseinsensitive"))
            throw new AssertionException("matching should be case-insensitive");

        if (!test.Matches("TestHarness.Test.SelectionTests"))
            throw new AssertionException("the assembly-qualified prefix should match");

        if (test.Matches("SelectionTests|Nothing"))
            throw new AssertionException(
                "alternation should not match: the filter is a substring, and a test claiming " +
                "otherwise would make the error message a lie");
    }
}
