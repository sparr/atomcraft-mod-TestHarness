using Atomcraft;

namespace Atomcraft.TestHarness;

/// <summary>
/// One logical per-cell channel, however the owning mod stores it.
///
/// The contract is deliberately <c>(x, y) -> value</c> rather than "hand me your array".
/// Most mods will not keep a full 6144x6144 array; they will keep something structured and
/// sparse, addressed by coordinate. A dictionary, a per-chunk map, a run-length store, and
/// a flat array all satisfy this identically, and the harness never learns which.
/// </summary>
/// <summary>
/// Storage shared by several channels, cleared once for all of them.
///
/// Channels are the unit a test thinks in, but they are often not the unit a mod stores. A mod
/// may register a stored value, a derived view of it, and a per-direction breakdown of the same
/// array, and clearing each independently re-clears what the previous call already did. Worse,
/// a derived channel has to nominate a Clear for storage it does not own, which is a small lie
/// in the registration.
///
/// Naming the shared storage once removes both problems: the group clears, the channels that
/// belong to it do not, and a read-only view can honestly say it owns nothing.
/// </summary>
public sealed class FieldGroup
{
    public required string Name { get; init; }

    /// <summary>Clears the whole group's storage over a rectangle, in one pass.</summary>
    public required Action<int, int, int, int> ClearRect { get; init; }

    /// <summary>
    /// Clears everything the group holds anywhere, plus any world-level aggregate that goes
    /// with it. Same reasoning as IFieldSpec.ClearEverything, at group scope.
    /// </summary>
    public Action? ClearEverything { get; init; }
}

public interface IFieldSpec
{
    string Name { get; }

    /// <summary>
    /// Shared storage this channel is a view of, or null when it owns its own. A channel in a
    /// group is not cleared individually; its group is.
    /// </summary>
    FieldGroup? Group => null;

    /// <summary>Rendered value at a world cell, for dumps and failure messages.</summary>
    string FormatAt(int worldX, int worldY);

    /// <summary>True when the cell holds nothing, so dumps can show sparsity honestly.</summary>
    bool IsUnsetAt(int worldX, int worldY);

    /// <summary>
    /// Reset a rectangle to empty. Not optional: per-test isolation is a lie if test N+1
    /// inherits test N's state.
    /// </summary>
    void ClearRect(int worldX, int worldY, int width, int height);

    /// <summary>
    /// Reset everything this channel holds, anywhere in the world, and any world-level
    /// aggregate that goes with it: a total, a dirty list, an index.
    ///
    /// ClearRect only reaches the region a test was handed, so state left outside it
    /// survives into the next test. That is harmless for a channel whose only consumer
    /// filters by the region, and silent for a channel with a running total. Defaults to a
    /// no-op so existing registrations are unaffected.
    /// </summary>
    void ClearEverything() { }

    /// <summary>
    /// A checksum over a rectangle of this channel, for determinism comparisons.
    ///
    /// The game's own chunk checksum covers material and heat, so a mod's per-cell state is
    /// invisible to it. That state is the likeliest place for order-dependence to hide: the
    /// material map is written under a partitioning the engine designed, while a mod's
    /// parallel structure has none of those guard rails.
    ///
    /// The default renders each cell, which works for any channel. A channel backed by a
    /// flat array can supply something faster.
    /// </summary>
    int ChecksumRect(int worldX, int worldY, int width, int height)
    {
        var sum = 0;
        for (var y = worldY; y < worldY + height; y++)
        for (var x = worldX; x < worldX + width; x++)
            sum = sum * 257 + (IsUnsetAt(x, y) ? 0 : FormatAt(x, y).GetHashCode());
        return sum;
    }
}

internal static class FieldSpecChecks
{
    /// <summary>
    /// True when a spec has no way to clear a rectangle. Only FieldSpec&lt;T&gt; can be in this
    /// state; a hand-written IFieldSpec must implement ClearRect to compile at all.
    /// </summary>
    public static bool ClearsNothing(this IFieldSpec spec) =>
        spec.GetType().GetProperty("Clear")?.GetValue(spec) == null
        && spec.GetType().IsGenericType
        && spec.GetType().GetGenericTypeDefinition() == typeof(FieldSpec<>);
}

/// <summary>A registered channel of <typeparamref name="T"/> per cell.</summary>
public sealed class FieldSpec<T> : IFieldSpec
{
    public required string Name { get; init; }

    /// <summary>Value at a world cell. Return <see cref="Unset"/> where nothing is stored.</summary>
    public required Func<int, int, T> Read { get; init; }

    /// <summary>Optional: tests that only observe a field do not need it writable.</summary>
    public Action<int, int, T>? Write { get; init; }

    /// <summary>
    /// Clears a rectangle. Required unless this channel belongs to a <see cref="Group"/>,
    /// which clears on its behalf. One or the other must be present: a channel that clears
    /// nothing leaks state into the next test.
    /// </summary>
    public Action<int, int, int, int>? Clear { get; init; }

    /// <summary>Shared storage this channel is a view of. See <see cref="FieldGroup"/>.</summary>
    public FieldGroup? Group { get; init; }

    /// <summary>
    /// Optional fast path for determinism checksums. The default walks the rectangle cell by
    /// cell through Read, which is the only thing that works for every storage shape and the
    /// wrong thing for a mod with a flat backing array and a lot of channels: thirteen
    /// channels over a one-chunk region is thirteen passes over 192x192 cells.
    /// </summary>
    public Func<int, int, int, int, int>? Checksum { get; init; }

    /// <summary>The "nothing here" value. Sparse stores need one; flat arrays usually have a natural zero.</summary>
    public T Unset { get; init; } = default!;

    /// <summary>Rendering for dumps. Defaults to ToString().</summary>
    public Func<T, string>? Format { get; init; }

    public T At(int worldX, int worldY) => Read(worldX, worldY);

    public void Put(int worldX, int worldY, T value)
    {
        if (Write == null)
            throw new AssertionException($"field '{Name}' is read-only");
        Write(worldX, worldY, value);
    }

    public string FormatAt(int worldX, int worldY)
    {
        var v = Read(worldX, worldY);
        return Format != null ? Format(v) : v?.ToString() ?? "";
    }

    public bool IsUnsetAt(int worldX, int worldY) =>
        EqualityComparer<T>.Default.Equals(Read(worldX, worldY), Unset);

    public void ClearRect(int worldX, int worldY, int width, int height)
    {
        if (Clear != null)
            Clear(worldX, worldY, width, height);
        else
            Group?.ClearRect(worldX, worldY, width, height);
    }

    public int ChecksumRect(int worldX, int worldY, int width, int height)
    {
        if (Checksum != null)
            return Checksum(worldX, worldY, width, height);

        var sum = 0;
        for (var y = worldY; y < worldY + height; y++)
        for (var x = worldX; x < worldX + width; x++)
            sum = sum * 257 + (IsUnsetAt(x, y) ? 0 : FormatAt(x, y).GetHashCode());
        return sum;
    }
}

/// <summary>
/// Every per-cell channel the harness knows about, including the game's own.
///
/// The three vanilla channels register through exactly the same public call a mod uses, so
/// there is no privileged path and no way for the mod-facing API to rot while the built-in
/// one keeps working.
/// </summary>
public static class FieldRegistry
{
    private static readonly Dictionary<string, IFieldSpec> Specs = new();
    private static readonly object Lock = new();
    private static bool _vanillaRegistered;

    public static void Register(IFieldSpec spec)
    {
        // Clear used to be a required init property, so the compiler enforced this. A channel
        // may now delegate to a group instead, which means the check moves to run time; a
        // channel that clears neither way would leak state into every later test.
        if (spec is { Group: null } && spec.ClearsNothing())
            throw new AssertionException(
                $"field '{spec.Name}' supplies neither Clear nor Group, so nothing would reset " +
                "it between tests. Give it a Clear, or put it in a FieldGroup with the other " +
                "channels that share its storage.");

        lock (Lock)
        {
            if (Specs.ContainsKey(spec.Name))
                throw new AssertionException($"field '{spec.Name}' is already registered");
            Specs[spec.Name] = spec;
        }
        Log.Info($"registered field '{spec.Name}'");
    }

    /// <summary>
    /// Drops a registration. A mod registers once at load and never needs this; a test that
    /// registers a channel of its own does, because anything left behind is cleared and
    /// checksummed for every test that follows.
    ///
    /// The other two registries have had this since they were written. This one did not,
    /// which made a channel registered by a test permanent.
    /// </summary>
    public static bool Unregister(string name)
    {
        lock (Lock) return Specs.Remove(name);
    }

    public static IFieldSpec Get(string name)
    {
        lock (Lock)
        {
            if (Specs.TryGetValue(name, out var spec))
                return spec;
            var known = Specs.Count == 0 ? "none" : string.Join(", ", Specs.Keys.OrderBy(k => k));
            throw new AssertionException($"no field named '{name}'. Registered: {known}");
        }
    }

    public static FieldSpec<T> Get<T>(string name) =>
        Get(name) as FieldSpec<T>
        ?? throw new AssertionException($"field '{name}' is not a field of {typeof(T).Name}");

    public static IReadOnlyList<IFieldSpec> All
    {
        get { lock (Lock) return Specs.Values.ToList(); }
    }

    /// <summary>
    /// Clears every registered channel over a rectangle, vanilla and mod alike.
    ///
    /// Grouped channels clear once per group rather than once per channel, which is the
    /// difference between one pass and thirteen for a mod whose channels are views of the
    /// same storage.
    /// </summary>
    public static void ClearAll(int worldX, int worldY, int width, int height)
    {
        var groups = new HashSet<FieldGroup>();

        foreach (var spec in All)
        {
            if (spec.Group is FieldGroup group)
            {
                if (groups.Add(group))
                    group.ClearRect(worldX, worldY, width, height);
            }
            else
            {
                spec.ClearRect(worldX, worldY, width, height);
            }
        }
    }

    /// <summary>
    /// One checksum per registered channel over a rectangle, for determinism comparisons.
    /// Keyed by channel name so a difference names the channel that moved.
    /// </summary>
    public static Dictionary<string, int> ChecksumAll(int worldX, int worldY, int width, int height)
    {
        var sums = new Dictionary<string, int>();
        foreach (var spec in All)
            sums[spec.Name] = spec.ChecksumRect(worldX, worldY, width, height);
        return sums;
    }

    /// <summary>Resets every registered channel completely. Called between tests.</summary>
    public static void ResetAll()
    {
        var groups = new HashSet<FieldGroup>();

        foreach (var spec in All)
        {
            if (spec.Group is FieldGroup group)
            {
                if (groups.Add(group))
                    group.ClearEverything?.Invoke();
            }
            else
            {
                spec.ClearEverything();
            }
        }
    }

    /// <summary>Registers the game's own three per-cell channels through the public path.</summary>
    public static void RegisterVanilla()
    {
        lock (Lock)
        {
            if (_vanillaRegistered)
                return;
            _vanillaRegistered = true;
        }

        // Resolved on every access, never captured. Loading a world replaces
        // Simulation.CurrentState with a new SimSnapshot, so a field reference captured at
        // registration goes stale the moment a session starts: writes land on the old array
        // while everything else reads the new one, and regions stop clearing.
        static SimField F() => Simulation.CurrentState?.Field
            ?? throw new AssertionException("Simulation.CurrentState.Field is null");

        Register(new FieldSpec<short>
        {
            Name = "core.material",
            Unset = -1,
            Read = (x, y) => F().MaterialTypeIdMap[F().Index(x, y)],
            Write = (x, y, v) => F().MaterialTypeIdMap[F().Index(x, y)] = v,
            Clear = (x, y, w, h) => FillShort(F().MaterialTypeIdMap, F(), x, y, w, h, -1),
            Format = id => id == -1 ? "." : id.ToMaterialName() ?? id.ToString(),
        });

        Register(new FieldSpec<short>
        {
            Name = "core.heat",
            Unset = 290,
            Read = (x, y) => F().HeatMap[F().Index(x, y)],
            Write = (x, y, v) => F().HeatMap[F().Index(x, y)] = v,
            Clear = (x, y, w, h) => FillShort(F().HeatMap, F(), x, y, w, h, 290),
            Format = k => k.ToString(),
        });

        // Registered for completeness, but the game never touches it: ElectricalCharge
        // appears nowhere in the assembly outside SimField's own accessors. Power is a
        // single global float, Simulation.ElectricityCurrentCharge, and circuit signals are
        // encoded in the material id, so Materials.IsWireOn reads a material rather than a
        // charge. A mod could use this array as free per-cell storage, though nothing
        // allocates or persists it.
        Register(new FieldSpec<short>
        {
            Name = "core.charge",
            Unset = 0,
            Read = (x, y) => F().ElectricalChargeMap[F().Index(x, y)],
            Write = (x, y, v) => F().ElectricalChargeMap[F().Index(x, y)] = v,
            Clear = (x, y, w, h) => FillShort(F().ElectricalChargeMap, F(), x, y, w, h, 0),
            Format = c => c.ToString(),
        });
    }

    private static void FillShort(short[] map, SimField field, int x0, int y0, int w, int h, short value)
    {
        for (var y = y0; y < y0 + h; y++)
        for (var x = x0; x < x0 + w; x++)
        {
            var i = field.Index(x, y);
            if (i >= 0 && i < map.Length)
                map[i] = value;
        }
    }
}
