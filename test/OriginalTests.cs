using System.Reflection;
using Atomcraft;
using HarmonyLib;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Calling a method as it was before it was patched, while it stays patched for everyone else.
///
/// The subject is RNG.Roll, chosen because it is what an RNG-modifying mod patches and because
/// it is called thousands of times per tick: if a technique is going to fail on a hot, small,
/// heavily inlined method, it fails here. The test installs a postfix of its own, so the
/// difference between the two paths is something this file created and can predict exactly.
/// </summary>
public static class OriginalTests
{
    private const int Sentinel = 4242;

    /// <summary>Filled with the game's own Roll IL by Original.Bind.</summary>
    private static int RollOriginal(int posX, int posY, int tick) =>
        throw new NotImplementedException("not bound");

    /// <summary>Replaces every roll with a value no real roll would produce.</summary>
    private static void RollPostfix(ref int __result) => __result = Sentinel;

    private static bool _prepared;
    private static Harmony? _harmony;

    private static void Prepare()
    {
        if (_prepared) return;
        _prepared = true;

        Original.Bind(typeof(RNG), nameof(RNG.Roll),
            AccessTools.Method(typeof(OriginalTests), nameof(RollOriginal)),
            new[] { typeof(int), typeof(int), typeof(int) });

        _harmony = new Harmony("sparr.TestHarness.Test.original");
        _harmony.Patch(
            AccessTools.Method(typeof(RNG), nameof(RNG.Roll),
                new[] { typeof(int), typeof(int), typeof(int) }),
            postfix: new HarmonyMethod(typeof(OriginalTests), nameof(RollPostfix)));
    }

    private static void Restore()
    {
        _harmony?.UnpatchAll("sparr.TestHarness.Test.original");
        _harmony = null;
    }

    /// <summary>
    /// The bound stub runs the unpatched code while the real method stays patched. Both halves
    /// are asserted: if the postfix were not applied, the stub returning a different value
    /// would prove nothing.
    /// </summary>
    [GameTest]
    public static void ABoundOriginalBypassesAPatchThatIsStillActive()
    {
        Prepare();
        try
        {
            var patched = RNG.Roll(12, 34, 56);
            if (patched != Sentinel)
                throw new AssertionException(
                    $"the test's own postfix is not applied (Roll returned {patched}), so this " +
                    "test cannot show that the original bypasses it");

            var original = RollOriginal(12, 34, 56);
            if (original == Sentinel)
                throw new AssertionException(
                    "the bound original returned the patched value, so it is running the patched " +
                    "method rather than a copy of the original");
        }
        finally
        {
            Restore();
        }
    }

    /// <summary>
    /// The original is the real thing, not merely something different: the same coordinates and
    /// tick give the same answer every time, and different ones do not all give one value.
    /// </summary>
    [GameTest]
    public static void ABoundOriginalReturnsTheGamesOwnAnswers()
    {
        Prepare();
        try
        {
            var first = RollOriginal(7, 9, 11);
            if (RollOriginal(7, 9, 11) != first)
                throw new AssertionException("the original is not deterministic for one input");

            var others = Enumerable.Range(0, 32).Select(i => RollOriginal(i, i * 3, 5)).Distinct().Count();
            if (others < 2)
                throw new AssertionException(
                    $"32 different inputs produced {others} distinct value(s); the stub is not " +
                    "running a real roll");
        }
        finally
        {
            Restore();
        }
    }

    /// <summary>
    /// Once unpatched, the real method agrees with the original again. This is what says the
    /// two paths are the same code rather than two implementations that merely differ.
    /// </summary>
    [GameTest]
    public static void TheTwoAgreeOnceThePatchIsGone()
    {
        Prepare();
        Restore();

        for (var i = 0; i < 8; i++)
            if (RNG.Roll(i, i + 1, i + 2) != RollOriginal(i, i + 1, i + 2))
                throw new AssertionException(
                    $"unpatched Roll and the bound original disagree at ({i},{i + 1},{i + 2})");
    }

    [GameTest]
    public static void AMismatchedStubIsRefused()
    {
        try
        {
            Original.Bind(typeof(RNG), nameof(RNG.Roll),
                AccessTools.Method(typeof(OriginalTests), nameof(WrongShape)),
                new[] { typeof(int), typeof(int), typeof(int) });

            throw new AssertionException("a stub with the wrong signature should have been refused");
        }
        catch (AssertionException e) when (e.Message.Contains("does not match"))
        {
            // expected
        }
    }

    private static int WrongShape(int only) => 0;

    [GameTest]
    public static void BindingTheSameTargetTwiceIsRefused()
    {
        Prepare();
        try
        {
            Original.Bind(typeof(RNG), nameof(RNG.Roll),
                AccessTools.Method(typeof(OriginalTests), nameof(RollOriginal)),
                new[] { typeof(int), typeof(int), typeof(int) });

            throw new AssertionException("binding the same target twice should have been refused");
        }
        catch (AssertionException e) when (e.Message.Contains("already has an original bound"))
        {
            // expected
        }
        finally
        {
            Restore();
        }
    }
}
