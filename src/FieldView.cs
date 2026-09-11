namespace Atomcraft.TestHarness;

/// <summary>
/// A registered channel seen through one region, in local coordinates.
///
/// This is what a mod's tests actually hold. It works the same whether the channel is a
/// flat array, a dictionary keyed by coordinate, or anything else, because the underlying
/// contract is only "give me the value at this cell".
/// </summary>
public sealed class FieldView<T>
{
    private readonly Region _region;
    private readonly FieldSpec<T> _spec;

    internal FieldView(Region region, FieldSpec<T> spec)
    {
        _region = region;
        _spec = spec;
    }

    public string Name => _spec.Name;

    public T this[int x, int y]
    {
        get { Bounds(x, y); return _spec.At(_region.OriginX + x, _region.OriginY + y); }
        set { Bounds(x, y); _spec.Put(_region.OriginX + x, _region.OriginY + y, value); }
    }

    public void Fill(int x, int y, int width, int height, T value)
    {
        for (var dy = 0; dy < height; dy++)
        for (var dx = 0; dx < width; dx++)
            this[x + dx, y + dy] = value;
    }

    public int Count(Func<T, bool> predicate)
    {
        var n = 0;
        for (var y = 0; y < _region.Height; y++)
        for (var x = 0; x < _region.Width; x++)
            if (predicate(this[x, y]))
                n++;
        return n;
    }

    /// <summary>Cells holding anything other than the channel's unset value.</summary>
    public int CountSet() =>
        Count(v => !EqualityComparer<T>.Default.Equals(v, _spec.Unset));

    public T Total(Func<T, T, T> add)
    {
        var acc = _spec.Unset;
        for (var y = 0; y < _region.Height; y++)
        for (var x = 0; x < _region.Width; x++)
            acc = add(acc, this[x, y]);
        return acc;
    }

    public void AssertAt(int x, int y, T expected)
    {
        var actual = this[x, y];
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new AssertionException(
                $"{_region.Name} field '{Name}' ({x},{y}): expected {expected}, got {actual}\n{_region.Dump(Name)}");
    }

    /// <summary>Every cell holds the same value. The usual shape of "nothing leaked in here".</summary>
    public void AssertUniform(T expected)
    {
        for (var y = 0; y < _region.Height; y++)
        for (var x = 0; x < _region.Width; x++)
            if (!EqualityComparer<T>.Default.Equals(this[x, y], expected))
                throw new AssertionException(
                    $"{_region.Name} field '{Name}': expected every cell to be {expected}, " +
                    $"but ({x},{y}) is {this[x, y]}\n{_region.Dump(Name)}");
    }

    /// <summary>
    /// Every cell is unset. AssertUniform(unset) tests the same thing, but its failure talks
    /// about a value when the question was about sparsity.
    /// </summary>
    public void AssertNoneSet()
    {
        var set = CountSet();
        if (set == 0)
            return;
        throw new AssertionException(
            $"{_region.Name} field '{Name}': expected nothing set, found {set} cell(s)\n{Dump()}");
    }

    public string Dump() => _region.Dump(Name);

    private void Bounds(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _region.Width || y >= _region.Height)
            throw new AssertionException(
                $"({x},{y}) is outside region '{_region.Name}' ({_region.Width}x{_region.Height})");
    }
}
