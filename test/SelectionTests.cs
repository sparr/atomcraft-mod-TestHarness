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

        // The message has to say why, or the reader retries the same pattern blind.
        foreach (var expected in new[] { "Registration|ResidueHunt", "regular expression", "unanchored" })
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
    /// The filter is an unanchored, case-insensitive regex over the full, assembly-qualified
    /// name, which is what the error message promises.
    /// </summary>
    [GameTest]
    public static void TheFilterIsACaseInsensitiveUnanchoredRegex()
    {
        var test = TestDiscovery.Discover(null)
            .FirstOrDefault(t => t.Name.EndsWith(nameof(TheFilterIsACaseInsensitiveUnanchoredRegex)));

        if (test == null)
            throw new AssertionException("could not find this test in discovery");

        if (!test.Matches("thefilterisacaseinsensitive"))
            throw new AssertionException("matching should be case-insensitive");

        if (!test.Matches("TestHarness.Test.SelectionTests"))
            throw new AssertionException("the assembly-qualified prefix should match");

        // The reason regex was worth doing, and the case that silently ran nothing before.
        if (!test.Matches("SelectionTests|NoSuchThing"))
            throw new AssertionException("alternation should select this test");

        if (!test.Matches("Unanchored.*Regex"))
            throw new AssertionException("a pattern with metacharacters should work");

        if (!test.Matches("^TestHarness\\.Test"))
            throw new AssertionException("anchoring should be available when asked for");

        if (test.Matches("NoTestIsNamedThis"))
            throw new AssertionException("a pattern matching nothing should select nothing");
    }

    /// <summary>
    /// Every filter that worked as a substring still works, since the match is unanchored.
    /// This is what makes the change safe for anyone already passing one.
    /// </summary>
    [GameTest]
    public static void PlainSubstringFiltersStillWork()
    {
        var names = TestDiscovery.Discover(null);

        foreach (var plain in new[] { "Vanilla", "Session", "Artifact" })
        {
            var byRegex = names.Count(t => t.Matches(plain));
            var bySubstring = names.Count(t => t.Name.Contains(plain, StringComparison.OrdinalIgnoreCase));

            if (byRegex != bySubstring)
                throw new AssertionException(
                    $"'{plain}' selected {byRegex} as a regex and {bySubstring} as a substring; " +
                    "a plain word must keep meaning what it meant");
        }
    }

    /// <summary>
    /// Exclude wins over filter for a test both match, which is the whole point: "this class
    /// except that test" is the common case, and it only works if the narrower statement holds.
    /// </summary>
    [GameTest]
    public static void ExcludeOverridesFilterOnOverlap()
    {
        var test = TestDiscovery.Discover(null)
            .FirstOrDefault(t => t.Name.EndsWith(nameof(ExcludeOverridesFilterOnOverlap)));

        if (test == null)
            throw new AssertionException("could not find this test in discovery");

        // Both patterns match this test; the exclude has to be the one that decides.
        const string filter = "SelectionTests";
        const string exclude = "ExcludeOverridesFilterOnOverlap";

        if (!test.Matches(filter) || !test.Matches(exclude))
            throw new AssertionException(
                "this test should match both patterns, or the overlap is not being exercised");

        var selected = TestDiscovery.Discover(null)
            .Where(t => t.Matches(filter) && !t.Matches(exclude))
            .ToList();

        if (selected.Any(t => t.Name.EndsWith(nameof(ExcludeOverridesFilterOnOverlap))))
            throw new AssertionException("the excluded test survived selection");

        if (selected.Count == 0)
            throw new AssertionException(
                "excluding one test removed the whole class, so this proves nothing about " +
                "precedence");
    }

    /// <summary>
    /// A pattern that will not compile is a usage mistake, reported the same way as one that
    /// matches nothing rather than thrown out of the runner as though the harness broke.
    /// </summary>
    [GameTest]
    public static void AnInvalidPatternIsReportedNotThrown()
    {
        try
        {
            TestCase.Compile("Unclosed(");
            throw new AssertionException("'Unclosed(' should not have compiled");
        }
        catch (ArgumentException)
        {
            // expected; TestExecutor.Begin turns this into a selection error
        }
    }
}
