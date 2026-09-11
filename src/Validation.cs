using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;

namespace Atomcraft.TestHarness;

/// <summary>Rules a mod can be checked against. Opt out of one by passing it to Check.</summary>
public enum Rule
{
    /// <summary>A material field names a material that does not exist.</summary>
    DanglingMaterialReference,

    /// <summary>A reaction names a material that does not exist.</summary>
    DanglingReactionMaterial,

    /// <summary>A craftable exists but is in no category, so it cannot be found in the UI.</summary>
    CraftableNotInAnyCategory,

    /// <summary>A ColorDelegate names a sampler that is not registered.</summary>
    UnknownColorDelegate,

    /// <summary>A LocIdName has no translation, so the raw key renders in the UI.</summary>
    MissingTranslation,

    /// <summary>A BaseMaterial subclass carries mutable instance state, which is shared.</summary>
    MutableMaterialState,

    /// <summary>DissolvesInto is declared. Unusable, and a crash if it ever took effect.</summary>
    DeclaresDissolvesInto,

    /// <summary>MaxTemperature is declared. Currently not enforced by the game.</summary>
    DeclaresMaxTemperature,

    /// <summary>A manifest names a data path that matches nothing in the zip.</summary>
    EmptyDataPath,
}

/// <summary>
/// How much a finding is worth. Ordered most severe first, because Check takes the least
/// severe level it should report and includes everything above it.
/// </summary>
public enum Severity
{
    /// <summary>The mod is broken. Check fails.</summary>
    Error,

    /// <summary>Worth knowing, not necessarily wrong. Logged, never fails.</summary>
    Warning,

    /// <summary>
    /// Style and risk, not a defect. Off unless asked for, because these rules cannot tell a
    /// deliberate choice from a mistake and a report full of false alarms is one nobody reads.
    /// </summary>
    Lint,
}

public sealed record Finding(Rule Rule, Severity Severity, string Subject, string Message)
{
    public override string ToString() =>
        $"{Severity.ToString().ToLowerInvariant()} {Rule} [{Subject}] {Message}";
}

/// <summary>
/// Generic checks any mod can run against itself, for the failures that produce no error at
/// load and no crash, just a mod that quietly does less than it says.
///
/// Everything here inspects the mod's own content only, never the vanilla game's. Several of
/// these rules have vanilla violations that a modder can do nothing about, and a report full
/// of those is a report nobody reads.
///
/// Runs after Game._Ready like any other test, so it compares what the mod shipped against
/// the live registries rather than predicting what the game would make of the JSON.
/// </summary>
public static class Validation
{
    /// <summary>
    /// Runs every rule and throws if any error survives. Warnings are logged and never fail.
    ///
    ///     [GameTest]
    ///     public static void ModIsWellFormed() => Validation.Check("MyMod");
    /// </summary>
    public static void Check(string modId, params Rule[] ignore) =>
        Check(modId, Severity.Warning, ignore);

    /// <summary>
    /// As Check, reporting down to the given level. Pass Severity.Lint to turn on every rule,
    /// including the ones that cannot distinguish a deliberate choice from a mistake.
    ///
    ///     [GameTest]
    ///     public static void ModIsWellFormed() => Validation.Check("MyMod", Severity.Lint);
    ///
    /// Lint findings are reported but never fail the check; only errors do.
    /// </summary>
    public static void Check(string modId, Severity include, params Rule[] ignore)
    {
        var findings = Inspect(modId, ignore).Where(f => f.Severity <= include).ToList();

        foreach (var f in findings.Where(f => f.Severity != Severity.Error))
            Log.Warn($"{modId}: {f}");

        var errors = findings.Where(f => f.Severity == Severity.Error).ToList();
        if (errors.Count == 0) return;

        var detail = string.Join("\n  ", errors.Select(e => e.ToString()));
        throw new AssertionException(
            $"{modId} failed validation with {errors.Count} error(s):\n  {detail}");
    }

    /// <summary>Same as Check, for a zip that is not installed or cannot be loaded.</summary>
    public static IReadOnlyList<Finding> InspectZip(string zipPath, params Rule[] ignore) =>
        Filter(ModContent.FromZip(zipPath), ignore);

    /// <summary>
    /// Every finding at every level, including lint, for a caller that wants to filter or
    /// report them itself. Check is the filtered view.
    /// </summary>
    public static IReadOnlyList<Finding> Inspect(string modId, params Rule[] ignore) =>
        Filter(ModContent.Load(modId), ignore);

    private static IReadOnlyList<Finding> Filter(ModContent content, Rule[] ignore)
    {
        var skip = new HashSet<Rule>(ignore);
        var findings = new List<Finding>();

        foreach (var finding in All(content))
            if (!skip.Contains(finding.Rule))
                findings.Add(finding);

        return findings;
    }

    private static IEnumerable<Finding> All(ModContent content)
    {
        foreach (var f in DataPaths(content)) yield return f;
        foreach (var f in MaterialReferences(content)) yield return f;
        foreach (var f in ReactionReferences(content)) yield return f;
        foreach (var f in Craftables(content)) yield return f;
        foreach (var f in ColorDelegates(content)) yield return f;
        foreach (var f in Translations(content)) yield return f;
        foreach (var f in MaterialState(content)) yield return f;
    }

    /// <summary>
    /// A manifest path that matches nothing in the zip. The loader reports no error for
    /// this, it simply loads no data, so the mod runs with none of the materials or
    /// reactions it believes it shipped. Anything that then looks one up by name gets a
    /// null, and the usual next line is a .Value that throws inside Materials.Init and takes
    /// Game._Ready down with it.
    /// </summary>
    private static IEnumerable<Finding> DataPaths(ModContent content)
    {
        foreach (var (moduleId, field, path) in content.EmptyDeclaredPaths)
            yield return new Finding(Rule.EmptyDataPath, Severity.Error, moduleId,
                $"mod.json declares {field} as '{path}', but the zip holds no .json under " +
                "that path, so none of it is loaded. Check the path against what your build " +
                "actually stages into the zip.");
    }

    /// <summary>
    /// Every material-name field on a shipped material. An unresolved name becomes -1, which
    /// is also air, so the game places nothing and reports nothing.
    /// </summary>
    private static IEnumerable<Finding> MaterialReferences(ModContent content)
    {
        foreach (var m in content.Materials)
        {
            var name = m.Name ?? "(unnamed)";

            foreach (var (field, value) in new (string, string?)[]
            {
                (nameof(m.PickUpInto), m.PickUpInto),
                (nameof(m.MinesInto), m.MinesInto),
                (nameof(m.BuildsInto), m.BuildsInto),
                (nameof(m.TurnsOnInto), m.TurnsOnInto),
                (nameof(m.TurnsOffInto), m.TurnsOffInto),
                (nameof(m.RotatesRightInto), m.RotatesRightInto),
                (nameof(m.RotatesLeftInto), m.RotatesLeftInto),
                (nameof(m.GrowsInto), m.GrowsInto),
                (nameof(m.TurnsIntoFromAlphaParticleImpact), m.TurnsIntoFromAlphaParticleImpact),
                (nameof(m.TurnsIntoFromProtonImpact), m.TurnsIntoFromProtonImpact),
                (nameof(m.TurnsIntoFromNeutronImpact), m.TurnsIntoFromNeutronImpact),
            })
            {
                if (!Missing(value)) continue;
                yield return new Finding(Rule.DanglingMaterialReference, Severity.Error, name,
                    $"{field} names '{value}', which is not a registered material");
            }

            foreach (var drop in m.DropRates?.Keys ?? Enumerable.Empty<string>())
            {
                if (!Missing(drop)) continue;
                yield return new Finding(Rule.DanglingMaterialReference, Severity.Error, name,
                    $"DropRates names '{drop}', which is not a registered material");
            }

            if (m.DissolvesInto is { Length: > 0 })
                yield return new Finding(Rule.DeclaresDissolvesInto, Severity.Error, name,
                    "declares DissolvesInto. This is a game bug, not a mistake in your mod: " +
                    "MaterialType's constructor discards the field, so it cannot be set from " +
                    "data at all, and BaseMaterial.TryDissolve overflows its coordinate " +
                    "buffer, so a material that does carry the value throws out of " +
                    "Simulation.Step the moment sulfuric acid touches it. Both were reported " +
                    "upstream on 2026-09-11. The field is unusable until they are fixed, so " +
                    "remove it rather than leaving it in as documentation of intent.");
        }
    }

    private static IEnumerable<Finding> ReactionReferences(ModContent content)
    {
        foreach (var r in content.Reactions)
        {
            var name = r.Name ?? "(unnamed)";

            if (Missing(r.PrimaryInput))
                yield return new Finding(Rule.DanglingReactionMaterial, Severity.Error, name,
                    $"PrimaryInput names '{r.PrimaryInput}', which is not a registered material. " +
                    "The loader logs this and drops the reaction, so it never fires.");

            foreach (var (role, names) in new (string, IEnumerable<string>?)[]
            {
                ("Inputs", r.Inputs?.Keys),
                ("Outputs", r.Outputs?.Keys),
                ("Catalysts", r.Catalysts?.Keys),
            })
            {
                foreach (var material in names ?? Enumerable.Empty<string>())
                {
                    if (!Missing(material)) continue;
                    yield return new Finding(Rule.DanglingReactionMaterial, Severity.Error, name,
                        $"{role} names '{material}', which is not a registered material");
                }
            }

            if (r.MaxTemperature.HasValue)
                yield return new Finding(Rule.DeclaresMaxTemperature, Severity.Lint, name,
                    $"declares MaxTemperature {r.MaxTemperature}, which the game does not " +
                    "currently enforce, so the reaction also fires above that temperature. " +
                    "The developer says the ceiling is deliberately disabled for now, so this " +
                    "is lint rather than an error: the declaration is harmless, documents intent " +
                    "for when it returns, and there is nothing your mod can do about it today.");
        }
    }

    /// <summary>
    /// Adding a craftable and putting it in a category are separate calls, and a craftable in
    /// no category exists, is craftable in principle, and cannot be found in the UI.
    /// </summary>
    private static IEnumerable<Finding> Craftables(ModContent content)
    {
        // CraftableList is private, and the public Get logs an error for every miss, which
        // would bury the run in noise for materials that were never meant to be craftable.
        var list = typeof(Atomcraft.Craftables)
            .GetField("CraftableList", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as System.Collections.IEnumerable;
        if (list == null) yield break;

        var craftableIds = new HashSet<int>();
        foreach (var craftable in list)
        {
            var id = craftable.GetType().GetField("MaterialTypeId")?.GetValue(craftable);
            if (id is short s) craftableIds.Add(s);
        }

        var categorized = new HashSet<int>();
        for (var i = 0; i < Atomcraft.Craftables.CategoryCount; i++)
            foreach (var id in Atomcraft.Craftables.GetCategory(i)?.MaterialTypeIds ?? [])
                categorized.Add(id);

        foreach (var m in content.Materials)
        {
            if (m.Name == null) continue;
            var id = Atomcraft.Materials.GetBaseMaterialId(m.Name);
            if (id == -1 || !craftableIds.Contains(id) || categorized.Contains(id)) continue;

            yield return new Finding(Rule.CraftableNotInAnyCategory, Severity.Error, m.Name,
                "has a crafting recipe but is in no category, so it never appears in the " +
                "crafting UI. Craftables.Add and adding the id to a category are separate steps.");
        }
    }

    private static IEnumerable<Finding> ColorDelegates(ModContent content)
    {
        foreach (var m in content.Materials)
        {
            if (string.IsNullOrEmpty(m.ColorDelegate)) continue;
            if (MaterialColorDelegates.Lookup?.ContainsKey(m.ColorDelegate) == true) continue;

            yield return new Finding(Rule.UnknownColorDelegate, Severity.Error, m.Name ?? "(unnamed)",
                $"ColorDelegate names '{m.ColorDelegate}', which is not registered in " +
                "MaterialColorDelegates.Lookup, so the material falls back to a flat color. " +
                "Register it from a postfix on MaterialColorDelegates.Init.");
        }
    }

    private static IEnumerable<Finding> Translations(ModContent content)
    {
        foreach (var m in content.Materials)
        {
            if (string.IsNullOrEmpty(m.LocIdName)) continue;
            // TranslationServer returns the key itself when it has no entry, which is also
            // exactly what the player would see in the UI.
            if (TranslationServer.Translate(m.LocIdName).ToString() != m.LocIdName) continue;

            yield return new Finding(Rule.MissingTranslation, Severity.Error, m.Name ?? "(unnamed)",
                $"LocIdName '{m.LocIdName}' has no translation, so the raw key renders in the " +
                "UI. The mod " + (content.TranslationKeys.Contains(m.LocIdName)
                    ? "does define this key, so its translations file may not be listed in mod.json"
                    : "does not define this key anywhere in its translations"));
        }
    }

    /// <summary>
    /// One BaseMaterial instance serves every pixel of its type, and Simulation.Step runs
    /// chunks in parallel, so a mutable instance field is shared mutable state across
    /// threads. The determinism mode catches this only when the race happens to bite; a field
    /// scan catches it every time.
    /// </summary>
    private static IEnumerable<Finding> MaterialState(ModContent content)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var simpleName = assembly.GetName().Name;
            if (simpleName == null || !content.AssemblyNames.Contains(simpleName)) continue;

            foreach (var type in Types(assembly))
            {
                if (!typeof(BaseMaterial).IsAssignableFrom(type) || type.IsAbstract) continue;

                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                     | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsInitOnly) continue;
                    // An auto-property's backing field is reported through the property below.
                    if (field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) continue;

                    yield return new Finding(Rule.MutableMaterialState, Severity.Lint, type.Name,
                        $"has a mutable instance field '{field.Name}'. BaseMaterial instances are " +
                        "shared by every pixel of the type and stepped from several threads at " +
                        "once, so per-pixel state belongs in the SimField maps or a registered " +
                        "field channel. Lint rather than error because a field assigned once in " +
                        "the constructor is configuration and perfectly safe; marking it readonly " +
                        "says so and silences this.");
                }

                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public
                                                            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (property.SetMethod == null) continue;

                    yield return new Finding(Rule.MutableMaterialState, Severity.Lint, type.Name,
                        $"has a settable instance property '{property.Name}'. BaseMaterial " +
                        "instances are shared by every pixel of the type and stepped from " +
                        "several threads at once. Lint rather than error for the same reason as " +
                        "fields: set once at construction it is configuration, not state.");
                }
            }
        }
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            // A mod referencing something absent still has usable types; report on those.
            return e.Types.Where(t => t != null)!;
        }
    }

    /// <summary>
    /// True when a name is present but resolves to nothing. GetBaseMaterialId reads
    /// BaseMaterialsDict, which is the dictionary SimField ids actually come from.
    /// </summary>
    private static bool Missing(string? name) =>
        !string.IsNullOrEmpty(name) && Atomcraft.Materials.GetBaseMaterialId(name) == -1;
}
