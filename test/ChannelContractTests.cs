namespace Atomcraft.TestHarness.Test;

/// <summary>
/// The contract check itself, which has to catch a broken channel and pass a sound one.
///
/// Asked for by the Pressure mod after 0.3 found by hand that ClearEverything was a silent
/// no-op for every FieldSpec channel. The value of a rule like this is entirely in whether it
/// fires, so each half is proved against a channel built to break it.
/// </summary>
public static class ChannelContractTests
{
    /// <summary>
    /// This mod's own channels. Check defaults to the caller's, which matters: the first run
    /// of this rule failed on twelve channels belonging to another installed mod, which is a
    /// true finding about that mod and no business of this test.
    /// </summary>
    [GameTest]
    public static void TheRegisteredChannelsHonorTheirContract(Region r)
    {
        ChannelContract.Check(r);
    }

    /// <summary>
    /// Scoping works: a survey of every channel sees more than this mod's own. Without this,
    /// the test above would pass equally well if Check silently inspected nothing.
    /// </summary>
    [GameTest]
    public static void CheckingOnlyTheCallersChannelsIsNarrowerThanCheckingAll(Region r)
    {
        var mine = ChannelContract.Inspect(r).Select(f => f.Subject).ToHashSet();
        var all = ChannelContract.InspectAll(r).Select(f => f.Subject).ToHashSet();

        if (!mine.IsSubsetOf(all))
            throw new AssertionException(
                "the caller-scoped check reported a channel the survey did not: " +
                string.Join(", ", mine.Except(all)));

        var owner = FieldRegistry.OwnerOf(ExampleChannel.Name);
        if (owner != "TestHarness.Test")
            throw new AssertionException(
                $"'{ExampleChannel.Name}' is recorded as owned by '{owner}', expected " +
                "'TestHarness.Test'. Ownership is how a mod avoids being failed by another " +
                "mod's channels.");
    }

    /// <summary>A channel whose Read and Write address different storage.</summary>
    [GameTest]
    public static void AChannelWhoseReadAndWriteDisagreeIsCaught(Region r)
    {
        const string name = "harness.badread";
        var store = new Dictionary<(int, int), int>();

        FieldRegistry.Register(new FieldSpec<int>
        {
            Name = name,
            Unset = 0,
            Write = (x, y, v) => store[(x, y)] = v,
            Read = (_, _) => 0,            // never reports what was written
            Clear = (_, _, _, _) => store.Clear(),
            ClearWorld = () => store.Clear(),
        });

        try
        {
            var findings = ChannelContract.Inspect(r, ExampleChannel.Name);
            Expect(findings, name, "Read returned");
        }
        finally
        {
            FieldRegistry.Unregister(name);
        }
    }

    /// <summary>
    /// A channel that declares a world-wide clear and does not deliver one. This is the exact
    /// bug the rule was requested for, and it is an error because the registration claims
    /// something untrue.
    /// </summary>
    [GameTest]
    public static void AWorldClearThatDoesNotClearIsCaught(Region r)
    {
        const string name = "harness.brokenworldclear";
        var store = new Dictionary<(int, int), int>();

        FieldRegistry.Register(new FieldSpec<int>
        {
            Name = name,
            Unset = 0,
            Write = (x, y, v) => store[(x, y)] = v,
            Read = (x, y) => store.TryGetValue((x, y), out var v) ? v : 0,
            Clear = (x, y, w, h) =>
            {
                for (var dy = 0; dy < h; dy++)
                for (var dx = 0; dx < w; dx++)
                    store.Remove((x + dx, y + dy));
            },
            ClearWorld = () => { },      // supplied, and does nothing
        });

        try
        {
            Expect(ChannelContract.Inspect(r, ExampleChannel.Name), name, "supplied but left");
        }
        finally
        {
            FieldRegistry.Unregister(name);
        }
    }

    /// <summary>
    /// Not declaring a world-wide clear at all is a warning, not an error. The game's own
    /// core.material and core.heat legitimately have none: they are backed by the SimField,
    /// which the world replaces, and clearing them world-wide between tests would wipe a live
    /// session's world.
    /// </summary>
    [GameTest]
    public static void AChannelWithNoWorldClearWarnsRatherThanFails(Region r)
    {
        const string name = "harness.noworldclear";
        var store = new Dictionary<(int, int), int>();

        FieldRegistry.Register(new FieldSpec<int>
        {
            Name = name,
            Unset = 0,
            Write = (x, y, v) => store[(x, y)] = v,
            Read = (x, y) => store.TryGetValue((x, y), out var v) ? v : 0,
            // Clears a rectangle but has no ClearWorld, so a world ending leaves this behind.
            Clear = (x, y, w, h) =>
            {
                for (var dy = 0; dy < h; dy++)
                for (var dx = 0; dx < w; dx++)
                    store.Remove((x + dx, y + dy));
            },
        });

        try
        {
            var finding = ChannelContract.Inspect(r, ExampleChannel.Name)
                .FirstOrDefault(f => f.Subject == name);

            if (finding == null)
                throw new AssertionException(
                    $"'{name}' has no world-wide clear and should have been reported");

            if (finding.Severity != Severity.Warning)
                throw new AssertionException(
                    $"expected a warning, got {finding.Severity}. Having no world clear is a " +
                    "legitimate choice for storage something else replaces, so failing on it " +
                    "would flag the game's own core channels.");

            if (!finding.Message.Contains("outlives the world"))
                throw new AssertionException($"unhelpful message: {finding.Message}");
        }
        finally
        {
            FieldRegistry.Unregister(name);
        }
    }

    /// <summary>
    /// A channel whose values cannot be probed is reported, not passed over. "Nothing was
    /// checked" and "everything was fine" must not look the same.
    /// </summary>
    [GameTest]
    public static void AnUnprobeableChannelIsReportedRatherThanSkipped(Region r)
    {
        const string name = "harness.unprobeable";

        FieldRegistry.Register(new FieldSpec<string>
        {
            Name = name,
            Unset = "",
            Read = (_, _) => "",
            Write = (_, _, _) => { },
            Clear = (_, _, _, _) => { },
        });

        try
        {
            var findings = ChannelContract.Inspect(r, ExampleChannel.Name);
            var finding = findings.FirstOrDefault(f => f.Subject == name);

            if (finding == null)
                throw new AssertionException(
                    $"'{name}' has no probe value for its type and should have been reported " +
                    "as unchecked");

            if (finding.Severity == Severity.Error)
                throw new AssertionException(
                    "being unprobeable is not a defect in the channel; it should warn, not fail");
        }
        finally
        {
            FieldRegistry.Unregister(name);
        }
    }

    private static void Expect(IReadOnlyList<Finding> findings, string subject, string mentions)
    {
        var finding = findings.FirstOrDefault(f => f.Subject == subject && f.Severity == Severity.Error);

        if (finding == null)
            throw new AssertionException(
                $"expected an error for '{subject}'. Got: " + (findings.Count == 0 ? "none"
                    : string.Join("; ", findings.Select(f => f.ToString()))));

        if (!finding.Message.Contains(mentions))
            throw new AssertionException(
                $"the message should name '{mentions}' so it is actionable. Got: {finding.Message}");
    }
}
