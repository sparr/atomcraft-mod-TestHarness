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
public interface IFieldSpec
{
    string Name { get; }

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

/// <summary>A registered channel of <typeparamref name="T"/> per cell.</summary>
public sealed class FieldSpec<T> : IFieldSpec
{
    public required string Name { get; init; }

    /// <summary>Value at a world cell. Return <see cref="Unset"/> where nothing is stored.</summary>
    public required Func<int, int, T> Read { get; init; }

    /// <summary>Optional: tests that only observe a field do not need it writable.</summary>
    public Action<int, int, T>? Write { get; init; }

    /// <summary>Clears a rectangle. Required, for the isolation reason above.</summary>
    public required Action<int, int, int, int> Clear { get; init; }

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

    public void ClearRect(int worldX, int worldY, int width, int height) =>
        Clear(worldX, worldY, width, height);
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
        lock (Lock)
        {
            if (Specs.ContainsKey(spec.Name))
                throw new AssertionException($"field '{spec.Name}' is already registered");
            Specs[spec.Name] = spec;
        }
        Log.Info($"registered field '{spec.Name}'");
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

    /// <summary>Clears every registered channel over a rectangle, vanilla and mod alike.</summary>
    public static void ClearAll(int worldX, int worldY, int width, int height)
    {
        foreach (var spec in All)
            spec.ClearRect(worldX, worldY, width, height);
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
        foreach (var spec in All)
            spec.ClearEverything();
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
