namespace Atomcraft.TestHarness;

/// <summary>
/// A piece of a mod's global state that is not addressed by coordinate.
///
/// <see cref="IFieldSpec"/> covers per-cell channels, which the harness can clear over a
/// rectangle because a test owns a rectangle. Plenty of mod state has no such shape: a
/// config flag, an inventory, a list of active things, a running total, a cache keyed by
/// something other than position. Contracts keeps ContractTypes, ActiveContracts, and an
/// Inventory as mutable statics; Pressure keeps configuration flags the same way. Neither
/// fits a field, and until now neither was reset between tests or seen by determinism.
///
/// The registration is deliberately about behavior rather than storage. A mod says how to
/// return the state to a clean slate and, optionally, how to summarize it; the harness never
/// learns what is behind it.
/// </summary>
public interface IStateSpec
{
    string Name { get; }

    /// <summary>
    /// Return this state to what it looks like after the mod initializes and before any test
    /// touches it. Not optional: per-test isolation is a lie if test N+1 inherits test N's
    /// state, and global state is the easiest kind to leak.
    ///
    /// Clean means usable, not empty. State loaded once at startup, a table of definitions
    /// read from JSON, should be left in place rather than cleared, since a mod cannot
    /// generally reload it mid-run.
    /// </summary>
    void Reset();

    /// <summary>
    /// A checksum for determinism comparisons, or null to sit out.
    ///
    /// Worth supplying for anything the simulation writes to, and not worth supplying for
    /// anything that only ever holds startup configuration. A mod's global state has none of
    /// the partitioning the engine applies to the grid, so a total accumulated from a
    /// parallel per-chunk pass is a likelier place for order-dependence to hide than the grid
    /// itself. Nothing else in the harness or the game will notice it diverging.
    /// </summary>
    int? Checksum() => null;

    /// <summary>A short rendering for failure messages. Defaults to nothing.</summary>
    string Describe() => "";
}

/// <summary>A registered piece of global state, described with delegates.</summary>
public sealed class StateSpec : IStateSpec
{
    public required string Name { get; init; }

    /// <summary>How to return this state to a clean, usable slate.</summary>
    public required Action OnReset { get; init; }

    /// <summary>Optional: include this state in determinism comparisons.</summary>
    public Func<int>? OnChecksum { get; init; }

    /// <summary>Optional: a short rendering for failure messages.</summary>
    public Func<string>? OnDescribe { get; init; }

    void IStateSpec.Reset() => OnReset();
    int? IStateSpec.Checksum() => OnChecksum?.Invoke();
    string IStateSpec.Describe() => OnDescribe?.Invoke() ?? "";
}

/// <summary>
/// Every piece of registered global state. The companion to <see cref="FieldRegistry"/>, for
/// the state a mod keeps that no rectangle describes.
/// </summary>
public static class StateRegistry
{
    private static readonly Dictionary<string, IStateSpec> Specs = new();
    private static readonly object Lock = new();

    public static void Register(IStateSpec spec)
    {
        lock (Lock)
        {
            if (Specs.ContainsKey(spec.Name))
                throw new AssertionException($"state '{spec.Name}' is already registered");
            Specs[spec.Name] = spec;
        }
        Log.Info($"registered state '{spec.Name}'");
    }

    /// <summary>
    /// Drops a registration. The mirror of TickRegistry.Unregister, and there for the same
    /// reason: a test that registers state of its own must be able to take it back out, or
    /// every later test in the run inherits it.
    /// </summary>
    public static bool Unregister(string name)
    {
        lock (Lock) return Specs.Remove(name);
    }

    public static IStateSpec Get(string name)
    {
        lock (Lock)
        {
            if (Specs.TryGetValue(name, out var spec))
                return spec;
            var known = Specs.Count == 0 ? "none" : string.Join(", ", Specs.Keys.OrderBy(k => k));
            throw new AssertionException($"no state named '{name}'. Registered: {known}");
        }
    }

    public static IReadOnlyList<IStateSpec> All
    {
        get { lock (Lock) return Specs.Values.ToList(); }
    }

    /// <summary>Returns every registered piece of state to a clean slate. Called between tests.</summary>
    public static void ResetAll()
    {
        foreach (var spec in All)
        {
            try
            {
                spec.Reset();
            }
            catch (Exception e)
            {
                // A throwing reset would otherwise abort the loop and leave the rest of the
                // registry dirty, which presents as an unrelated test failing later.
                throw new AssertionException(
                    $"resetting state '{spec.Name}' threw {e.GetType().Name}: {e.Message}. " +
                    "Every later test in this run would have inherited its state.");
            }
        }
    }

    /// <summary>
    /// One checksum per registered state that supplies one, keyed by name so a difference
    /// names what moved. State that returns null sits out.
    /// </summary>
    public static Dictionary<string, int> ChecksumAll()
    {
        var sums = new Dictionary<string, int>();
        foreach (var spec in All)
            if (spec.Checksum() is int sum)
                sums[spec.Name] = sum;
        return sums;
    }

    /// <summary>Every registered state's description, for a failure message.</summary>
    public static string Dump()
    {
        var lines = All
            .Select(s => $"  {s.Name}: {s.Describe()}")
            .Where(line => !line.EndsWith(": "))
            .ToList();
        return lines.Count == 0 ? "" : "registered mod state:\n" + string.Join("\n", lines);
    }
}
