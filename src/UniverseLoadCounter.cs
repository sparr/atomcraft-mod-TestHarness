using Atomcraft;
using HarmonyLib;

namespace Atomcraft.TestHarness;

/// <summary>
/// Counts real reads of a universe save file.
///
/// FileManager.TryLoadUniverseFile is the only path that parses the file, and it is also the
/// method the mod loader hangs OnUniverseLoad on, so counting it answers both "did we reload"
/// and "would a mod's load hook have run". It is private, hence the string name.
/// </summary>
[HarmonyPatch(typeof(FileManager), "TryLoadUniverseFile")]
internal static class UniverseLoadCounter
{
    private static void Postfix(bool __result)
    {
        if (__result) Session.RecordUniverseLoad();
    }
}
