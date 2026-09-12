namespace Atomcraft.TestHarness.Test;

/// <summary>
/// A test that has looked and found nothing to judge.
///
/// Asked for by a conformance suite that measures whether the game's RNG behaves like a
/// probability. One of its tests compares live rolls against the stock lookup table; if the
/// game's RNG is not that table, the comparison has no baseline and the test has nothing to
/// say. Returning early recorded a pass, which is a lie in the direction that matters: a green
/// run is supposed to mean the thing was judged.
/// </summary>
public static class InapplicableTests
{
    [GameTest]
    public static void InapplicableIsNotAPass()
    {
        var thrown = false;
        try
        {
            Harness.Inapplicable("nothing to compare against");
        }
        catch (InapplicableException e)
        {
            thrown = true;
            if (e.Message != "nothing to compare against")
                throw new AssertionException($"the reason was not carried: {e.Message}");
        }

        if (!thrown)
            throw new AssertionException(
                "Inapplicable returned instead of throwing, so a test calling it would carry on " +
                "and be recorded as a pass");
    }

    /// <summary>
    /// The one that proves the executor treats it as its own status. It reports inapplicable,
    /// so the run must show a nonzero inapplicable count and this test must not appear as
    /// passed.
    /// </summary>
    [GameTest]
    public static void ThisTestDeclinesToJudgeAnything()
    {
        Harness.Inapplicable(
            "this test exists to be counted as inapplicable; if it shows up as passed, the " +
            "executor is treating no-verdict as a verdict");
    }

    /// <summary>
    /// And an inapplicable verdict is not a failure either. Without this the status could be
    /// implemented by failing, which would be honest but would make a conformance suite
    /// unusable against a game build it cannot judge.
    /// </summary>
    [GameTest]
    public static void AnInapplicableTestDoesNotFailTheRun()
    {
        // The suite this test belongs to has passing tests in it, so the run as a whole is
        // judged and exits 0 even though the test above declined. That combination is the
        // whole point: some verdicts, some abstentions.
        if (TestExecutor.ExitCode != 0)
            throw new AssertionException(
                "the run is already failing, so this test cannot show that an abstention alone " +
                "does not fail it");
    }
}
