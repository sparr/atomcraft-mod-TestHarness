namespace Atomcraft.TestHarness;

/// <summary>
/// Thrown by a test that has looked and found there was nothing here to judge.
///
/// Distinct from Skip, which is decided when the test is written, and distinct from a pass,
/// which claims the thing was judged and found good. A conformance suite pointed at an
/// arbitrary game build needs this: if the precondition its comparison rests on is absent,
/// the honest answer is "no verdict", and returning early would record a pass for a test that
/// measured nothing.
/// </summary>
public sealed class InapplicableException : Exception
{
    public InapplicableException(string reason) : base(reason) { }
}
