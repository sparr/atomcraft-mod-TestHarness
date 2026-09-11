using Atomcraft;
using Godot;

namespace Atomcraft.TestHarness;

/// <summary>
/// Where in the world a region sits. Absolute Y is semantically loaded, and the choice is a
/// real trade-off rather than a formality. Ambient temperatures below are measured, not
/// assumed; run the harness with --atomtest-diagnose to re-measure after a game update.
///
/// Region.Clear leaves a flat 290 K regardless of band, because a known starting point is
/// what makes tests reproducible. Where that differs from the band's ambient, heat drifts
/// toward ambient over a long run. Use PinnedHeat for temperature-sensitive work, or
/// FillAmbientHeat to start in equilibrium.
/// </summary>
public enum Altitude
{
    /// <summary>
    /// y &gt; 4800, below the workshop line. Ambient about 1573 K.
    ///
    /// The default, because it is the only band where the simulation leaves a region alone:
    /// above the workshop line the game spontaneously condenses noble gases out of air
    /// roughly once per 24 ticks in a chunk-sized region, which shows up as pixels the test
    /// never placed. The price is that ambient is far above the 290 K Clear leaves behind,
    /// so anything running for hundreds of ticks warms noticeably.
    /// </summary>
    Deep = 5000,

    /// <summary>
    /// Ambient about 295 K, close to what Clear leaves, so almost no thermal drift.
    ///
    /// Use for long temperature-sensitive runs, but expect occasional noble gas pixels to
    /// condense out of air: this band is above the workshop line.
    /// </summary>
    Underground = 3000,

    /// <summary>
    /// Ambient about 284 K, and inside the weather range. Only for tests that need rain,
    /// snow, or sky. Weather is pinned sunny before every test, but this is the band where
    /// that pinning matters.
    /// </summary>
    Surface = 2000,
}

/// <summary>
/// A private rectangle of the world grid, with a clock the test drives by hand.
///
/// The world field is 6144x6144 and fully allocated as air from static init, long before
/// any session exists, so a test can take a slice of it without loading a world. Stepping
/// goes through <see cref="Simulation.SimulateQuadrant"/>, which is the same per-cell code
/// path the real simulation uses, but bounded to this rectangle and single-threaded.
///
/// Coordinates passed to this class are local: (0, 0) is the region's top-left corner.
/// </summary>
public sealed class Region
{
    /// <summary>
    /// The engine's unit of work. Simulation.Step iterates 64x64 chunks and splits each into
    /// four 32x32 quadrants; caches are keyed by chunk origin. Regions are therefore whole
    /// chunks on chunk boundaries, so a test exercises the same partitioning the real
    /// simulation does rather than an arbitrary rectangle that straddles it.
    /// </summary>
    public const int ChunkSize = 64;

    /// <summary>Chunks of air kept between regions so neighbors cannot interact.</summary>
    private const int SpacingChunks = 1;
    private const int Margin = SpacingChunks * ChunkSize;

    /// <summary>
    /// How far outside the region per-tick update flags are cleared.
    ///
    /// This is deliberately much smaller than the spacing between regions. Movement code
    /// such as ConveyorMaterial.TryConvey and BaseMaterial.SwapWithTarget reads the target
    /// cell's flag, and a target can lie just outside the region, so a stale flag there
    /// would wrongly block movement. A few cells covers every single-step reach; clearing
    /// the whole spacing margin costs nine times the region itself every tick and buys
    /// nothing, since nothing out there is ever simulated.
    /// </summary>
    private const int FlagMargin = 8;

    private static readonly Dictionary<Altitude, int> NextX = new();
    private static readonly object AllocLock = new();

    /// <summary>
    /// Resolved per access. A session replaces Simulation.CurrentState, so a snapshot held
    /// from allocation time would silently refer to a world that is no longer the one being
    /// simulated.
    /// </summary>
    private static SimSnapshot _state => Simulation.CurrentState
        ?? throw new AssertionException("Simulation.CurrentState is null");

    public string Name { get; }
    public int OriginX { get; }
    public int OriginY { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// The absolute simulation tick. Behavior genuinely depends on its value, not merely on
    /// how many ticks have elapsed: vanilla picks a rasterization order from it, and machine
    /// pixels commonly gate on tick parity. Tests that care should set it explicitly.
    /// </summary>
    public int Tick
    {
        get => _state.Tick;
        set => _state.Tick = value;
    }

    private Region(string name, int x, int y, int w, int h)
    {
        Name = name;
        OriginX = x;
        OriginY = y;
        Width = w;
        Height = h;
    }

    /// <summary>Chunks wide and tall. Cell dimensions are these times <see cref="ChunkSize"/>.</summary>
    public int ChunksWide => Width / ChunkSize;
    public int ChunksTall => Height / ChunkSize;

    /// <summary>
    /// Returns every band's space for reuse. Called when a test finishes.
    ///
    /// Without it the cursor only ever advances, so a one-chunk region costs 128 cells of a
    /// 6144-wide world for the lifetime of the process and a band holds about forty-eight
    /// tests ever. A large enough suite then fails to allocate, and the failure lands on
    /// whichever tests happen to sort last rather than on whatever grew the suite.
    ///
    /// Releasing is safe because a region is cleared, margin included, the moment it is
    /// handed out, and because only one is live at a time. Capacity becomes a question of
    /// how many regions a single test holds rather than how many tests exist.
    /// </summary>
    public static void ReleaseAll()
    {
        lock (AllocLock)
            NextX.Clear();
    }

    public static Region Allocate(string name, int chunksWide, int chunksTall,
        Altitude band = Altitude.Deep, string? wall = null, int wallThickness = 1)
    {
        var state = Simulation.CurrentState
            ?? throw new AssertionException("Simulation.CurrentState is null; the game has not initialized");

        if (chunksWide < 1 || chunksTall < 1)
            throw new AssertionException($"region '{name}' must be at least one chunk");

        var width = chunksWide * ChunkSize;
        var height = chunksTall * ChunkSize;

        var y = (int)band;
        y -= y % ChunkSize;

        if (width + Margin * 2 >= state.Field.Width || y + height + Margin >= state.Field.Height)
            throw new AssertionException(
                $"region '{name}' is too large for the {band} band: {chunksWide}x{chunksTall} chunks " +
                $"in a {state.Field.Width}x{state.Field.Height} world");

        int x;
        lock (AllocLock)
        {
            NextX.TryGetValue(band, out var cursor);
            if (cursor == 0)
                cursor = ChunkSize;           // leave the world edge alone

            if (cursor + width + Margin >= state.Field.Width)
                throw new AssertionException(
                    $"the {band} band is full: no room for '{name}' after " +
                    $"{(cursor - ChunkSize) / ChunkSize} chunk(s) already allocated. " +
                    "Regions are released when a test finishes, so this means a single test " +
                    "asked for more than the band holds.");

            x = cursor;
            NextX[band] = cursor + width + Margin;
        }

        // Snap to a chunk boundary: the engine's caches and quadrant split assume it.
        x -= x % ChunkSize;

        var region = new Region(name, x, y, width, height);
        region.Clear();
        if (wall != null)
            region.BuildWalls(wall, wallThickness);
        return region;
    }

    /// <summary>
    /// Lines the region with a sealed box, so liquid and gas tests do not each have to
    /// build their own container. Use a static material: a solid wall falls down.
    /// </summary>
    public void BuildWalls(string material, int thickness = 1)
    {
        var id = Lookup(material);
        if (thickness * 2 >= Width || thickness * 2 >= Height)
            throw new AssertionException($"walls {thickness} thick leave no interior in '{Name}'");

        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            if (x < thickness || y < thickness || x >= Width - thickness || y >= Height - thickness)
                SetRaw(x, y, id);

        WallThickness = thickness;
    }

    /// <summary>Thickness of the wall built at allocation, or 0. Interior spans [Wall, Size-Wall).</summary>
    public int WallThickness { get; private set; }

    /// <summary>
    /// Resets every registered channel over the region and its margin, mod fields included.
    /// A test that inherits the previous test's gas is not an isolated test.
    /// </summary>
    public void Clear()
    {
        var x0 = OriginX - Margin;
        var y0 = OriginY - Margin;
        var w = Width + Margin * 2;
        var h = Height + Margin * 2;

        FieldRegistry.ClearAll(x0, y0, w, h);

        var f = _state.Field;
        for (var y = y0; y < y0 + h; y++)
        for (var x = x0; x < x0 + w; x++)
        {
            var i = f.Index(x, y);
            if (i >= 0 && i < f.UpdatedWithinCurrentTick.Length)
                f.UpdatedWithinCurrentTick[i] = false;
        }
    }

    // --- registered fields ------------------------------------------------------------

    /// <summary>A typed view of a registered channel, in this region's local coordinates.</summary>
    public FieldView<T> Field<T>(string name) => new(this, FieldRegistry.Get<T>(name));

    /// <summary>Glyph dump of any registered channel, including a mod's own.</summary>
    public string Dump(string fieldName)
    {
        var spec = FieldRegistry.Get(fieldName);
        var sb = new System.Text.StringBuilder();
        sb.Append($"  region {Name} field '{fieldName}' at ({OriginX},{OriginY}) {Width}x{Height} tick {Tick}\n");

        // Width the widest rendered value, so numeric channels line up in columns.
        var cells = new string[Height, Width];
        var widest = 1;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var text = spec.IsUnsetAt(OriginX + x, OriginY + y) ? "." : spec.FormatAt(OriginX + x, OriginY + y);
            cells[y, x] = text;
            widest = Math.Max(widest, text.Length);
        }

        for (var y = 0; y < Height; y++)
        {
            sb.Append("  ");
            for (var x = 0; x < Width; x++)
                sb.Append(cells[y, x].PadLeft(widest)).Append(widest > 1 ? " " : "");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // --- painting ---------------------------------------------------------------------

    /// <summary>
    /// Places a pixel, optionally at a given temperature.
    ///
    /// Spawning cold and then heating makes the pixel pass through every temperature band
    /// on the way up, which can fire reactions the test was not asking about and leaves the
    /// result depending on how long the ramp took. Giving a temperature sets material and
    /// heat together, so the pixel exists only at the temperature under test.
    /// </summary>
    public void Set(int x, int y, string material, short? kelvin = null)
    {
        var id = Lookup(material);
        if (kelvin is short k)
        {
            Bounds(x, y);
            _state.Field.SetMaterialAndHeat(Index(x, y), id, k);
        }
        else
        {
            SetRaw(x, y, id);
        }
    }

    /// <summary>
    /// Pushes the pixel at (fromX, fromY) toward (toX, toY) exactly as a conveyor does, and
    /// returns whether it moved.
    ///
    /// This is the only practical way to fire a material's OnImpact. Falling does not call it:
    /// the game's own explosive-on-landing material, Nitroglycerin, hand-rolls a check inside
    /// its StepLiquid rather than relying on OnImpact, and the only callers in the shipped
    /// assembly are ConveyorMaterial.TryConvey and the four Rubber materials.
    ///
    /// The semantics are the conveyor's, so they are worth stating plainly. OnImpact fires on
    /// the material occupying the TARGET cell, is passed the source coordinates first, and
    /// happens only when the target is occupied. A move into air returns true and fires
    /// nothing.
    ///
    /// The return value describes the conveyor, not the outcome. It is false whenever the
    /// target was occupied, including when the impact handler then moved pixels itself:
    /// conveying water into an Allow Liquids filter returns false and still passes the water
    /// through to the far side. So assert on the field, never on this bool.
    ///
    /// One thing a region test cannot do this way is detonate something. Igniting a material
    /// whose Ignition.Explodes is set reads Avatars.LocalAvatar.PlayerId, which is null
    /// without a session, so it throws a NullReferenceException from inside the game.
    /// </summary>
    public bool ConveyInto(int fromX, int fromY, int toX, int toY)
    {
        Bounds(fromX, fromY);
        Bounds(toX, toY);

        // ignoreUpdatedFlag because a test-driven convey is not part of a tick's scan, so
        // the per-tick flag carries no meaning here and a stale one from whatever ran last
        // would silently turn this into a no-op.
        return ConveyorMaterial.TryConvey(
            OriginX + fromX, OriginY + fromY,
            OriginX + toX, OriginY + toY,
            _state.Field, Tick, ignoreUpdatedFlag: true);
    }

    /// <summary>
    /// Clears a cell to air. SetRaw(x, y, -1) does the same thing but reads like a raw-id
    /// escape hatch rather than the ordinary act of punching a hole in a painted layout.
    /// </summary>
    public void SetAir(int x, int y) => SetRaw(x, y, -1);

    public void FillAir(int x, int y, int width, int height)
    {
        for (var dy = 0; dy < height; dy++)
        for (var dx = 0; dx < width; dx++)
            SetRaw(x + dx, y + dy, -1);
    }

    /// <summary>
    /// Writes a material id straight into the field.
    ///
    /// Documented behavior, not an accident: this bypasses any Harmony patch a mod has on
    /// SimField's mutators, so test setup does not look like gameplay to the mod under
    /// test. That is what lets a test fabricate a state the mod's own write path would
    /// never produce, such as stale per-cell data on a cell that has since been emptied.
    /// Use Session.SetPixel instead when a test wants the mod to observe the write.
    /// </summary>
    public void SetRaw(int x, int y, short materialTypeId)
    {
        Bounds(x, y);
        _state.Field.MaterialTypeIdMap[Index(x, y)] = materialTypeId;
    }

    public void Fill(int x, int y, int width, int height, string material, short? kelvin = null)
    {
        var id = Lookup(material);
        for (var dy = 0; dy < height; dy++)
        for (var dx = 0; dx < width; dx++)
        {
            if (kelvin is short k)
            {
                Bounds(x + dx, y + dy);
                _state.Field.SetMaterialAndHeat(Index(x + dx, y + dy), id, k);
            }
            else
            {
                SetRaw(x + dx, y + dy, id);
            }
        }
    }

    /// <summary>
    /// The temperature the game itself would place this material at in a given row: its
    /// declared DefaultTemperature, or the ambient for that altitude when it has none.
    ///
    /// The row is required rather than defaulted, because ambient varies continuously with
    /// depth, not merely by band. A region several chunks tall spans hundreds of cells, so
    /// a single answer for the whole region would be wrong everywhere except one row.
    /// </summary>
    public short PlacementTemperature(string material, int y) =>
        Lookup(material).GetMaterialPlacementTemperature(OriginY + y);

    /// <summary>Ambient temperature the game would use for a row of this region.</summary>
    public short AmbientAt(int y) => (OriginY + y).GetAmbientTemperatureHeatmapValue();

    /// <summary>
    /// Fills the region with the ambient temperature the game would use at each row, rather
    /// than the flat value <see cref="Clear"/> leaves behind.
    ///
    /// Clear deliberately gives a uniform, known temperature so tests are reproducible, but
    /// that is not what a real world looks like at depth. Anything sensitive to ambient
    /// should ask for the real profile.
    /// </summary>
    public void FillAmbientHeat()
    {
        for (var y = 0; y < Height; y++)
        {
            var ambient = AmbientAt(y);
            for (var x = 0; x < Width; x++)
                SetHeat(x, y, ambient);
        }
    }

    /// <summary>Paints from ASCII art. Diffable, and it lives next to the assertion.</summary>
    public void Paint(string art, Dictionary<char, string> legend, int atX = 0, int atY = 0)
    {
        var rows = art.Trim('\n').Split('\n');
        for (var y = 0; y < rows.Length; y++)
        {
            var row = rows[y].TrimEnd('\r');
            for (var x = 0; x < row.Length; x++)
            {
                var c = row[x];
                if (c == '.' || c == ' ')
                    continue;
                if (!legend.TryGetValue(c, out var material))
                    throw new AssertionException($"no legend entry for '{c}' in region '{Name}'");
                Set(atX + x, atY + y, material);
            }
        }
    }

    public void SetHeat(int x, int y, short kelvin)
    {
        Bounds(x, y);
        _state.Field.HeatMap[Index(x, y)] = kelvin;
    }

    public void FillHeat(short kelvin)
    {
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            SetHeat(x, y, kelvin);
    }

    // --- reading ----------------------------------------------------------------------

    public short RawAt(int x, int y)
    {
        Bounds(x, y);
        return _state.Field.MaterialTypeIdMap[Index(x, y)];
    }

    /// <summary>Material name at a cell, or null for air.</summary>
    public string? At(int x, int y)
    {
        var id = RawAt(x, y);
        return id == -1 ? null : id.ToMaterialName();
    }

    public short HeatAt(int x, int y)
    {
        Bounds(x, y);
        return _state.Field.HeatMap[Index(x, y)];
    }

    public int Count(string material)
    {
        var id = Lookup(material);
        var n = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            if (RawAt(x, y) == id)
                n++;
        return n;
    }

    public int CountAir()
    {
        var n = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            if (RawAt(x, y) == -1)
                n++;
        return n;
    }

    // --- stepping ---------------------------------------------------------------------

    /// <summary>
    /// Advances the simulation by <paramref name="count"/> ticks over this region only.
    ///
    /// Single-threaded and bounded, so it is deterministic and cheap, and it does not
    /// disturb any other region. The rasterization order is fixed rather than tick-derived:
    /// vanilla varies it to avoid directional bias over a whole world, but a test wants the
    /// same answer every run.
    /// </summary>
    /// <summary>
    /// Re-applied to every cell after each tick while set. Null lets heat evolve normally.
    ///
    /// Heat decays toward ambient on a hashed schedule, so setting a temperature once and
    /// then running for a while does not hold it: a region set to 400 K drifts back to
    /// roughly 290 K, which silently moves it inside any temperature-gated recipe you were
    /// trying to stay outside of.
    /// </summary>
    public short? PinnedHeat { get; set; }

    public void Ticks(int count)
    {
        var window = new RectInt(OriginX, OriginY, Width, Height);
        var order = new[] { 0, 1, 2, 3 };
        var before = Tick;
        var half = ChunkSize / 2;

        for (var i = 0; i < count; i++)
        {
            ClearUpdateFlags();

            // Mirrors Simulation.Step: one quadrant index at a time across every chunk,
            // with each chunk split into four 32x32 quadrants. Vanilla runs the chunks in
            // parallel; running them in order here is what makes a test reproducible.
            for (var quadrant = 0; quadrant < 4; quadrant++)
            for (var cy = 0; cy < ChunksTall; cy++)
            for (var cx = 0; cx < ChunksWide; cx++)
            {
                var chunkX = OriginX + cx * ChunkSize;
                var chunkY = OriginY + cy * ChunkSize;
                var minX = chunkX + (quadrant % 2 == 0 ? 0 : half);
                var minY = chunkY + (quadrant < 2 ? 0 : half);
                Simulation.SimulateQuadrant(_state, minX, minY, minX + half, minY + half,
                    window, order, Tick % 4);
            }

            // Mod passes run after the game's quadrant passes and before heat is pinned
            // again, bounded to this region so a pass cannot act outside the test's world.
            TickRegistry.StepAll(window, Tick);

            if (PinnedHeat is short kelvin)
                FillHeat(kelvin);

            Tick++;
        }

        if (Tick != before + count)
            throw new AssertionException(
                $"region '{Name}' did not advance: expected tick {before + count}, got {Tick}");
    }

    /// <summary>
    /// Steps until <paramref name="condition"/> holds, and fails if it never does.
    ///
    /// Preferred over a fixed tick count. It states the expectation the test actually has
    /// ("the grain reaches the floor") instead of a number someone guessed, it stops as
    /// soon as the behavior happens rather than always paying for the worst case, and the
    /// failure says how long it waited.
    /// </summary>
    /// <returns>Ticks elapsed before the condition held.</returns>
    public int TicksUntil(Func<bool> condition, int maxTicks, string? expectation = null)
    {
        if (condition())
            return 0;

        for (var i = 1; i <= maxTicks; i++)
        {
            Ticks(1);
            if (condition())
                return i;
        }

        var what = expectation ?? "the expected condition";
        throw new AssertionException(
            $"{Name}: {what} did not hold within {maxTicks} ticks\n{Dump()}");
    }

    /// <summary>Steps while the condition holds, failing if it never stops holding.</summary>
    public int TicksWhile(Func<bool> condition, int maxTicks, string? expectation = null) =>
        TicksUntil(() => !condition(), maxTicks, expectation);

    private void ClearUpdateFlags()
    {
        var f = _state.Field;
        for (var y = OriginY - FlagMargin; y < OriginY + Height + FlagMargin; y++)
        for (var x = OriginX - FlagMargin; x < OriginX + Width + FlagMargin; x++)
        {
            var i = f.Index(x, y);
            if (i >= 0 && i < f.UpdatedWithinCurrentTick.Length)
                f.UpdatedWithinCurrentTick[i] = false;
        }
    }

    /// <summary>
    /// A checksum over the region's material and heat, using the game's own
    /// SimSnapshot.GetChunkChecksum. That is the function multiplayer uses to detect
    /// desync, so a difference here is a difference the game itself would call a divergence.
    ///
    /// Note it reads 65x65 cells per chunk, one row and column into the neighbor, which is
    /// the game's own off-by-one. The margin around a region is cleared air, so it stays
    /// deterministic.
    /// </summary>
    public int Checksum()
    {
        var combined = 0;
        for (var cy = 0; cy < ChunksTall; cy++)
        for (var cx = 0; cx < ChunksWide; cx++)
            combined = combined * 31 + _state.GetChunkChecksum(OriginX + cx * ChunkSize, OriginY + cy * ChunkSize);
        return combined;
    }

    // --- assertions -------------------------------------------------------------------

    public void AssertAt(int x, int y, string? expected)
    {
        var actual = At(x, y);
        if (actual != expected)
            throw new AssertionException(
                $"{Name} ({x},{y}): expected {Describe(expected)}, got {Describe(actual)}\n{Dump()}");
    }

    public void AssertCount(string material, int expected)
    {
        var actual = Count(material);
        if (actual == expected)
            return;

        // "found 0" is ambiguous between "it was consumed" and "it fell out of the region",
        // which are very different bugs. Check the surrounding margin and say which.
        var escaped = CountInMargin(material);
        var note = escaped > 0
            ? $" {escaped} found in the surrounding margin: the material left the region. " +
              "Allocate with a wall to contain it."
            : "";
        throw new AssertionException(
            $"{Name}: expected {expected} x {material}, found {actual}.{note}\n{Dump()}");
    }

    /// <summary>Occurrences in the air gap around the region, i.e. things that escaped.</summary>
    public int CountInMargin(string material)
    {
        var id = Lookup(material);
        var f = _state.Field;
        var n = 0;
        for (var y = OriginY - Margin; y < OriginY + Height + Margin; y++)
        for (var x = OriginX - Margin; x < OriginX + Width + Margin; x++)
        {
            if (x >= OriginX && x < OriginX + Width && y >= OriginY && y < OriginY + Height)
                continue;
            var i = f.Index(x, y);
            if (i >= 0 && i < f.MaterialTypeIdMap.Length && f.MaterialTypeIdMap[i] == id)
                n++;
        }
        return n;
    }

    private static string Describe(string? material) => material ?? "air";

    // --- rendering --------------------------------------------------------------------

    /// <summary>One glyph per cell plus a legend, for failure messages.</summary>
    public string Dump()
    {
        var glyphs = new Dictionary<short, char>();
        var legend = new List<string>();
        var next = 0;
        const string alphabet = "#o*+x=%&@$?!~^";

        var sb = new System.Text.StringBuilder();
        sb.Append($"  region {Name} at ({OriginX},{OriginY}) {Width}x{Height} tick {Tick}\n");
        for (var y = 0; y < Height; y++)
        {
            sb.Append("  ");
            for (var x = 0; x < Width; x++)
            {
                var id = RawAt(x, y);
                if (id == -1) { sb.Append('.'); continue; }
                if (!glyphs.TryGetValue(id, out var g))
                {
                    g = next < alphabet.Length ? alphabet[next] : '?';
                    next++;
                    glyphs[id] = g;
                    legend.Add($"{g} {id.ToMaterialName()}");
                }
                sb.Append(g);
            }
            sb.Append('\n');
        }
        if (legend.Count > 0)
            sb.Append("  legend: ").Append(string.Join(", ", legend)).Append('\n');
        return sb.ToString();
    }

    // --- internals --------------------------------------------------------------------

    private int Index(int x, int y) => _state.Field.Index(OriginX + x, OriginY + y);

    private void Bounds(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            throw new AssertionException($"({x},{y}) is outside region '{Name}' ({Width}x{Height})");
    }

    /// <summary>
    /// Resolves a name to the id the simulation field actually stores.
    ///
    /// The game keeps two id spaces. Materials.GetMaterialTypeId reads MaterialTypesDict;
    /// ToMaterialName and every Step path read BaseMaterialsDict. They are assigned
    /// independently and do not agree. SimField holds base material ids, so using a
    /// material type id here silently places a different material: "Granite" became
    /// "Compost" and fell, because one is static and the other is a solid.
    /// </summary>
    private static short Lookup(string material)
    {
        var id = Materials.GetBaseMaterialId(material);
        if (id != -1)
            return id;

        // 1913 materials with names like "Limestone Wall" and "Granite Gravel" make typos
        // and near-misses the common case, so say what was probably meant.
        var near = new List<string>();
        for (short candidate = 0; candidate < Materials.Count && near.Count < 6; candidate++)
        {
            var n = candidate.ToMaterialName();
            if (string.IsNullOrEmpty(n))
                continue;
            if (n.Contains(material, StringComparison.OrdinalIgnoreCase)
                || material.Contains(n, StringComparison.OrdinalIgnoreCase))
                near.Add(n);
        }
        var hint = near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : "";
        throw new AssertionException($"no such material: '{material}'.{hint}");
    }
}
