namespace Atomcraft.TestHarness.Refused;

/// <summary>
/// Asks for a harness version that cannot exist, so this mod is refused on every run.
///
/// The point is to have a genuinely refused mod present, so the harness can assert that a
/// refusal is recorded against the right assembly. Identifying the caller is the part most
/// likely to break, since it walks the stack for the first frame outside the harness.
/// </summary>
public static class ModEntry
{
    public const string ImpossibleVersion = "0.0";

    public static void Initialize() => Harness.RequireVersion(ImpossibleVersion);
}
