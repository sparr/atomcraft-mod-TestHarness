using Atomcraft;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Global state a mod keeps that no rectangle describes: a config flag, an inventory, a
/// running total. Contracts and Pressure both have some, and before StateRegistry existed
/// neither was cleared between tests nor seen by determinism.
/// </summary>
public static class StateRegistryTests
{
    /// <summary>Stands in for a mod's mutable static. Deliberately not per-cell.</summary>
    private static int _total;
    private static bool _flag;

    private const string Name = "testharness.total";

    /// <summary>
    /// Used only by the dirty/reset pair below, which must survive across two tests and so
    /// cannot share a registration with anything that cleans up after itself.
    /// </summary>
    private const string AcrossTests = "testharness.across-tests";

    private static IStateSpec Spec(string name = Name) => new StateSpec
    {
        Name = name,
        OnReset = () => { _total = 0; _flag = false; },
        OnChecksum = () => _total * 31 + (_flag ? 1 : 0),
        OnDescribe = () => $"total={_total} flag={_flag}",
    };

    [GameTest]
    public static void ResetReturnsStateToACleanSlate()
    {
        StateRegistry.Register(Spec());
        try
        {
            _total = 42;
            _flag = true;

            StateRegistry.ResetAll();

            if (_total != 0 || _flag)
                throw new AssertionException($"reset left total={_total} flag={_flag}");
        }
        finally
        {
            StateRegistry.Unregister(Name);
        }
    }

    /// <summary>
    /// The opt-out works: ResetModState = false leaves registered state exactly as the
    /// previous test left it.
    ///
    /// Named to sort between the test that dirties the state and the test that checks it was
    /// cleaned, so the three form a chain: dirty, observe it survived an opted-out test,
    /// observe the harness cleaned it for the next one. This test is also what proves the
    /// next one is not vacuous, since seeing 99 here and 0 there can only mean the executor
    /// did the clearing.
    /// </summary>
    [GameTest(ResetModState = false)]
    public static void StateIsNotResetWhenOptedOut()
    {
        if (_total != 99 || !_flag)
            throw new AssertionException(
                $"expected the previous test's state to survive ResetModState = false, " +
                $"got total={_total} flag={_flag}");
    }

    /// <summary>
    /// The point of the whole thing, and the only test here that would catch the executor
    /// failing to call ResetAll at all.
    ///
    /// Depends on StateIsDirtiedForTheNextTest running IMMEDIATELY before it. Tests run in
    /// name order within a class, and these two names are adjacent on purpose: StateIsD
    /// sorts directly before StateIsR with nothing able to fall between them. Adjacency is
    /// the point, not tidiness. An earlier version of this pair was merely "somewhere
    /// before", and ResetReturnsStateToACleanSlate landed in the gap, called ResetAll itself,
    /// and left this test passing on another test's cleanup while proving nothing about the
    /// executor.
    ///
    /// The dependence is asserted rather than assumed too: if the order ever changes this
    /// fails saying so, instead of passing because it measured nothing.
    /// </summary>
    [GameTest]
    public static void StateIsResetBetweenTests()
    {
        try
        {
            StateRegistry.Get(AcrossTests);
        }
        catch (AssertionException)
        {
            throw new AssertionException(
                $"'{AcrossTests}' is not registered, so StateIsDirtiedForTheNextTest has not run " +
                "yet and this test proves nothing. Tests are expected to run in name order.");
        }

        try
        {
            if (_total != 0 || _flag)
                throw new AssertionException(
                    $"inherited dirty state from an earlier test: total={_total} flag={_flag}. " +
                    "TestExecutor should have called StateRegistry.ResetAll before this test.");
        }
        finally
        {
            StateRegistry.Unregister(AcrossTests);
        }
    }

    /// <summary>
    /// Leaves state dirty and registered, for StateIsResetBetweenTests to catch. Named to
    /// sort immediately before it. Deliberately cleans up nothing: the harness is what is
    /// under test here, and a finally would hide whether it did its job.
    /// </summary>
    [GameTest]
    public static void StateIsDirtiedForTheNextTest()
    {
        StateRegistry.Register(Spec(AcrossTests));
        _total = 99;
        _flag = true;
    }

    [GameTest]
    public static void ChecksumsEnterTheDeterminismComparison()
    {
        StateRegistry.Register(Spec());
        try
        {
            _total = 7;
            var before = StateRegistry.ChecksumAll();

            if (!before.TryGetValue(Name, out var first))
                throw new AssertionException(
                    $"'{Name}' supplies a checksum but did not appear in ChecksumAll");

            _total = 8;
            var after = StateRegistry.ChecksumAll();

            if (after[Name] == first)
                throw new AssertionException(
                    "the checksum did not change when the state did, so determinism would " +
                    "never notice this state diverging");
        }
        finally
        {
            StateRegistry.Unregister(Name);
        }
    }

    /// <summary>
    /// State that sits out determinism, which is the right choice for anything holding only
    /// startup configuration.
    /// </summary>
    [GameTest]
    public static void StateWithoutAChecksumSitsOut()
    {
        const string name = "testharness.nochecksum";
        StateRegistry.Register(new StateSpec { Name = name, OnReset = () => { } });
        try
        {
            if (StateRegistry.ChecksumAll().ContainsKey(name))
                throw new AssertionException(
                    $"'{name}' supplies no checksum and should not appear in ChecksumAll");
        }
        finally
        {
            StateRegistry.Unregister(name);
        }
    }

    /// <summary>
    /// A throwing reset has to name itself. Left unreported it presents as an unrelated test
    /// failing later, because every test after it inherits whatever was not cleaned up.
    /// </summary>
    [GameTest]
    public static void AThrowingResetNamesTheStateThatThrew()
    {
        const string name = "testharness.throws";
        StateRegistry.Register(new StateSpec
        {
            Name = name,
            OnReset = () => throw new InvalidOperationException("deliberate"),
        });

        try
        {
            StateRegistry.ResetAll();
            throw new AssertionException("ResetAll should have propagated the failure");
        }
        catch (AssertionException e) when (e.Message.Contains(name))
        {
            // expected: the message names the offending state
        }
        finally
        {
            StateRegistry.Unregister(name);
        }
    }

    [GameTest]
    public static void RegisteringTheSameNameTwiceIsRefused()
    {
        StateRegistry.Register(Spec());
        try
        {
            StateRegistry.Register(Spec());
            throw new AssertionException("registering a duplicate name should have been refused");
        }
        catch (AssertionException e) when (e.Message.Contains("already registered"))
        {
            // expected
        }
        finally
        {
            StateRegistry.Unregister(Name);
        }
    }
}
