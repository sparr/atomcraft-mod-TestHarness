using System.Reflection;

namespace Atomcraft.TestHarness;

/// <summary>
/// Exercises a registered channel instead of inspecting its declaration.
///
/// Asked for by the Pressure mod, and the case for it is the bug that prompted it: the
/// interface declared ClearEverything, the sealed class every mod registers through exposed
/// no delegate for it, so the world-scoped reset was a silent no-op for every FieldSpec
/// channel in existence and nothing failed anywhere. A rule that wrote a value and then
/// cleared it would have said so immediately, and it generalizes to any future member a
/// registration can decline to supply.
///
/// Needs a region because it writes: the cells it touches must belong to the running test, or
/// it would clobber whatever else is in the world.
/// </summary>
public static class ChannelContract
{
    /// <summary>
    /// Checks every registered channel that can be written, and throws if any fails. Channels
    /// whose value type this cannot synthesize a probe for are reported as skipped rather than
    /// silently passed, since "nothing was checked" and "everything was fine" must not look
    /// the same.
    /// </summary>
    public static void Check(Region r, params string[] ignore) =>
        Report(Inspect(r, ignore));

    /// <summary>
    /// As Check, but for every registered channel rather than only the caller's own. Useful
    /// for a survey; a mod's own test should not fail because another mod's channel is
    /// imperfect.
    /// </summary>
    public static IReadOnlyList<Finding> InspectAll(Region r, params string[] ignore) =>
        Inspect(r, owner: null, ignore);

    public static IReadOnlyList<Finding> Inspect(Region r, params string[] ignore) =>
        Inspect(r, Caller(), ignore);

    private static IReadOnlyList<Finding> Inspect(Region r, string? owner, string[] ignore)
    {
        var skip = new HashSet<string>(ignore);
        var findings = new List<Finding>();

        foreach (var spec in FieldRegistry.All)
        {
            if (skip.Contains(spec.Name)) continue;
            if (owner != null && FieldRegistry.OwnerOf(spec.Name) != owner) continue;

            var type = spec.GetType();
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(FieldSpec<>))
                continue;   // a hand-written IFieldSpec implements the members or fails to compile

            foreach (var finding in CheckOne(spec, type, r))
                findings.Add(finding);
        }

        return findings;
    }

    private static IEnumerable<Finding> CheckOne(IFieldSpec spec, Type type, Region r)
    {
        var valueType = type.GetGenericArguments()[0];
        var write = type.GetProperty("Write")?.GetValue(spec);
        if (write == null)
            yield break;    // read-only views are legitimate and have nothing to exercise

        if (!TryProbe(valueType, type.GetProperty("Unset")?.GetValue(spec), out var probe))
        {
            yield return new Finding(Rule.ChannelContract, Severity.Warning, spec.Name,
                $"cannot be exercised: no probe value for {valueType.Name}. The contract is " +
                "unchecked for this channel rather than confirmed.");
            yield break;
        }

        // A cell inside the running test's own region, so nothing outside it is disturbed.
        var x = r.OriginX;
        var y = r.OriginY;

        var put = type.GetMethod("Put")!;
        var at = type.GetMethod("At")!;

        put.Invoke(spec, new[] { (object)x, y, probe });

        var readBack = at.Invoke(spec, new object[] { x, y });
        if (!Equals(readBack, probe))
        {
            yield return new Finding(Rule.ChannelContract, Severity.Error, spec.Name,
                $"Write stored {probe} but Read returned {readBack ?? "null"}. The two do not " +
                "address the same storage, so every assertion made through this channel is " +
                "about something other than what the mod wrote.");
            yield break;
        }

        spec.ClearRect(x, y, 1, 1);
        if (!spec.IsUnsetAt(x, y))
            yield return new Finding(Rule.ChannelContract, Severity.Error, spec.Name,
                $"ClearRect left {at.Invoke(spec, new object[] { x, y })} at a cell it cleared. " +
                "Per-test isolation depends on this, so the next test inherits it.");

        // ClearEverything is the one that was a silent no-op for every FieldSpec channel until
        // ClearWorld existed, so it is checked rather than trusted. But not every channel
        // should have one: the game's own core.material and core.heat are backed by the
        // SimField, which the world replaces, and a world-wide clear of them between tests
        // would wipe a session's world. So declaring one and having it not work is an error,
        // while not declaring one at all is worth saying once and no more.
        var declaresWorldClear = type.GetProperty("ClearWorld")?.GetValue(spec) != null
                                 || spec.Group?.ClearEverything != null;

        put.Invoke(spec, new[] { (object)x, y, probe });
        spec.ClearEverything();

        if (!spec.IsUnsetAt(x, y))
            yield return declaresWorldClear
                ? new Finding(Rule.ChannelContract, Severity.Error, spec.Name,
                    "ClearEverything is supplied but left a written cell in place, so the " +
                    "channel says it can reset world-wide and does not.")
                : new Finding(Rule.ChannelContract, Severity.Warning, spec.Name,
                    "has no world-wide clear, so its state outlives the world it belongs to " +
                    "and survives between tests. Supply ClearWorld on the spec, or " +
                    "ClearEverything on its group, unless the storage is owned by something " +
                    "that replaces it anyway, as the game's own core channels are.");

        spec.ClearRect(x, y, 1, 1);
    }

    /// <summary>
    /// The assembly that called in, so a mod checks its own channels by default. The same
    /// stack walk RequireVersion uses, and for the same reason: the registry records who
    /// registered what, but the caller has to be identified to match against it.
    /// </summary>
    private static string? Caller() =>
        new System.Diagnostics.StackTrace().GetFrames()
            ?.Select(f => f.GetMethod()?.DeclaringType?.Assembly)
            .FirstOrDefault(a => a != null && a != typeof(ChannelContract).Assembly)
            ?.GetName().Name;

    /// <summary>
    /// A value distinguishable from Unset. Numeric and boolean channels cover everything
    /// registered so far; anything else is reported unchecked rather than guessed at.
    /// </summary>
    private static bool TryProbe(Type valueType, object? unset, out object probe)
    {
        probe = null!;
        try
        {
            if (valueType == typeof(bool))
                probe = !(unset is bool b && b);
            else if (valueType.IsPrimitive || valueType == typeof(decimal))
                probe = Convert.ChangeType(Equals(unset, Convert.ChangeType(7, valueType)) ? 9 : 7, valueType);
            else
                return false;
        }
        catch
        {
            return false;
        }

        return !Equals(probe, unset);
    }

    private static void Report(IReadOnlyList<Finding> findings)
    {
        foreach (var w in findings.Where(f => f.Severity != Severity.Error))
            Log.Warn(w.ToString());

        var errors = findings.Where(f => f.Severity == Severity.Error).ToList();
        if (errors.Count == 0) return;

        throw new AssertionException(
            $"{errors.Count} registered channel(s) do not honor the contract:\n  " +
            string.Join("\n  ", errors.Select(e => e.ToString())));
    }
}
