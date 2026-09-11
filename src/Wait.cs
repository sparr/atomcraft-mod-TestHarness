namespace Atomcraft.TestHarness;

/// <summary>
/// What a frame-driven test yields to say how long to pause.
///
/// A test that needs the engine to do something, load a world, write a save, run its own
/// frame callbacks, cannot do it inside one call: the game only makes progress between
/// frames. Such a test returns IEnumerator and yields one of these.
/// </summary>
public abstract class Wait
{
    /// <summary>True when the harness may resume the test.</summary>
    public abstract bool Done { get; }

    /// <summary>Shown if the wait never finishes.</summary>
    public abstract string Describe();

    /// <summary>Called once per engine frame while waiting.</summary>
    public virtual void Tick() { }

    public static Wait Frames(int count) => new FrameWait(count);
    public static Wait NextFrame => new FrameWait(1);
    public static Wait Until(Func<bool> condition, string what) => new ConditionWait(condition, what);

    private sealed class FrameWait : Wait
    {
        private int _remaining;
        private readonly int _total;
        public FrameWait(int count) { _remaining = count; _total = count; }
        public override bool Done => _remaining <= 0;
        public override void Tick() => _remaining--;
        public override string Describe() => $"{_total} frame(s)";
    }

    private sealed class ConditionWait : Wait
    {
        private readonly Func<bool> _condition;
        private readonly string _what;
        public ConditionWait(Func<bool> condition, string what) { _condition = condition; _what = what; }
        public override bool Done => _condition();
        public override string Describe() => _what;
    }
}
