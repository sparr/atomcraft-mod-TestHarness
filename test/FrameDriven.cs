using System.Collections;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Tests that run across engine frames rather than inside one call.
///
/// Anything involving a session, a world load, or a save has to work this way: the game
/// only makes progress between frames, so a test that calls into it and then immediately
/// asserts sees the state it started with.
/// </summary>
public static class FrameDriven
{
    [GameTest]
    public static IEnumerator AFrameDrivenTestResumesAfterYielding(Region r)
    {
        var startFrame = Runner.Frame;

        yield return Wait.NextFrame;
        if (Runner.Frame <= startFrame)
            throw new AssertionException("the test resumed without a frame having elapsed");

        yield return Wait.Frames(3);
        if (Runner.Frame < startFrame + 4)
            throw new AssertionException(
                $"expected at least 4 frames to pass, saw {Runner.Frame - startFrame}");
    }

    /// <summary>
    /// Region state survives across a yield, so a frame-driven test can set something up,
    /// let the game run, and assert afterwards.
    /// </summary>
    [GameTest]
    public static IEnumerator RegionStateSurvivesAYield(Region r)
    {
        r.Set(4, 4, "Granite");
        yield return Wait.Frames(2);
        r.AssertAt(4, 4, "Granite");
    }

    /// <summary>
    /// Waiting on a condition that never becomes true fails with what it was waiting for,
    /// rather than hanging the suite. Short timeout so the check itself is quick.
    /// </summary>
    [GameTest(TimeoutFrames = 5, Skip = "demonstrates the timeout path; unskip by hand to see it fail")]
    public static IEnumerator AWaitThatNeverFinishesFails(Region r)
    {
        yield return Wait.Until(() => false, "something that never happens");
    }
}
