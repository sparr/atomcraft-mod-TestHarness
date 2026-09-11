using Godot;

namespace Atomcraft.TestHarness.Test;

/// <summary>
/// Every rule, proved against a mod built to break it.
///
/// A rule that has only ever returned nothing is indistinguishable from a rule that inspects
/// nothing, and the sweep across the installed mods returned nothing for most of these. So
/// each one gets a fixture with exactly one planted fault and an assertion that it fires.
///
/// The fixture is written at test time rather than checked in, so the fault and the assertion
/// are readable together. It is written outside the Mods directories and inspected with
/// InspectZip, so nothing here is ever loaded by the game.
/// </summary>
public static class ValidationTests
{
    /// <summary>A name no material will ever have, for the dangling-reference rules.</summary>
    private const string Absent = "TestHarness No Such Material";

    [GameTest]
    public static void DanglingMaterialReferenceIsCaught()
    {
        var findings = Inspect(materials: $$"""
            [{ "Name": "TestHarness Probe", "MinesInto": "{{Absent}}" }]
            """);

        Expect(findings, Rule.DanglingMaterialReference, Severity.Error, "MinesInto");
    }

    [GameTest]
    public static void DanglingDropRateIsCaught()
    {
        var findings = Inspect(materials: $$"""
            [{ "Name": "TestHarness Probe", "DropRates": { "{{Absent}}": 1 } }]
            """);

        Expect(findings, Rule.DanglingMaterialReference, Severity.Error, "DropRates");
    }

    [GameTest]
    public static void DanglingReactionMaterialIsCaught()
    {
        var findings = Inspect(reactions: $$"""
            [{ "Name": "TestHarness Probe Reaction", "PrimaryInput": "Water",
               "Inputs": { "Water": 1 }, "Outputs": { "{{Absent}}": 1 } }]
            """);

        Expect(findings, Rule.DanglingReactionMaterial, Severity.Error, "Outputs");
    }

    [GameTest]
    public static void DanglingPrimaryInputIsCaught()
    {
        var findings = Inspect(reactions: $$"""
            [{ "Name": "TestHarness Probe Reaction", "PrimaryInput": "{{Absent}}",
               "Inputs": { "Water": 1 }, "Outputs": { "Steam": 1 } }]
            """);

        Expect(findings, Rule.DanglingReactionMaterial, Severity.Error, "PrimaryInput");
    }

    [GameTest]
    public static void UnknownColorDelegateIsCaught()
    {
        var findings = Inspect(materials: """
            [{ "Name": "TestHarness Probe", "ColorDelegate": "TestHarnessNoSuchSampler" }]
            """);

        Expect(findings, Rule.UnknownColorDelegate, Severity.Error, "TestHarnessNoSuchSampler");
    }

    [GameTest]
    public static void MissingTranslationIsCaught()
    {
        var findings = Inspect(materials: """
            [{ "Name": "TestHarness Probe", "LocIdName": "testharness.NO_SUCH_KEY" }]
            """);

        Expect(findings, Rule.MissingTranslation, Severity.Error, "testharness.NO_SUCH_KEY");
    }

    [GameTest]
    public static void DeclaredDissolvesIntoIsAnErrorAndSaysItIsAGameBug()
    {
        var findings = Inspect(materials: """
            [{ "Name": "TestHarness Probe", "DissolvesInto": [ "Water" ] }]
            """);

        var finding = Expect(findings, Rule.DeclaresDissolvesInto, Severity.Error, "game bug");

        // The whole point of this rule is that the modder did nothing wrong, so the message
        // has to name both defects rather than just refusing the field.
        foreach (var expected in new[] { "TryDissolve", "cannot be set from" })
            if (!finding.Message.Contains(expected))
                throw new AssertionException(
                    $"the message should mention '{expected}' so the modder can see this is " +
                    $"not their mistake. Got: {finding.Message}");
    }

    [GameTest]
    public static void DeclaredMaxTemperatureIsLintNotError()
    {
        var findings = Inspect(reactions: """
            [{ "Name": "TestHarness Probe Reaction", "PrimaryInput": "Water",
               "Inputs": { "Water": 1 }, "Outputs": { "Steam": 1 },
               "Temperature": 300, "MaxTemperature": 400 }]
            """);

        Expect(findings, Rule.DeclaresMaxTemperature, Severity.Lint, "MaxTemperature");
    }

    /// <summary>
    /// The rule that found the real fault in Contracts, whose materials and reactions both
    /// point at directories its build does not produce.
    /// </summary>
    [GameTest]
    public static void EmptyDataPathIsCaught()
    {
        var zip = Fixture(materialsPath: "Data/Materials/", materials: null);
        var findings = Validation.InspectZip(zip);

        Expect(findings, Rule.EmptyDataPath, Severity.Error, "Data/Materials/");
    }

    /// <summary>
    /// A clean mod produces nothing. Without this, every test above would still pass if the
    /// rules simply fired on everything.
    /// </summary>
    [GameTest]
    public static void AWellFormedModProducesNoFindings()
    {
        var findings = Inspect(
            materials: """
                [{ "Name": "TestHarness Probe", "MinesInto": "Water", "State": 0 }]
                """,
            reactions: """
                [{ "Name": "TestHarness Probe Reaction", "PrimaryInput": "Water",
                   "Inputs": { "Water": 1 }, "Outputs": { "Steam": 1 } }]
                """);

        if (findings.Count != 0)
            throw new AssertionException(
                "a well formed mod produced findings:\n  " +
                string.Join("\n  ", findings.Select(f => f.ToString())));
    }

    /// <summary>
    /// Severity filtering, since lint being off by default is the contract a mod relies on
    /// when it decides not to fix a lint finding.
    /// </summary>
    [GameTest]
    public static void LintIsExcludedByDefaultAndNeverFails()
    {
        var zip = Fixture(materials: null, reactions: """
            [{ "Name": "TestHarness Probe Reaction", "PrimaryInput": "Water",
               "Inputs": { "Water": 1 }, "Outputs": { "Steam": 1 },
               "Temperature": 300, "MaxTemperature": 400 }]
            """);

        var all = Validation.InspectZip(zip);
        if (!all.Any(f => f.Severity == Severity.Lint))
            throw new AssertionException("the fixture produced no lint finding, so this proves nothing");

        if (all.Any(f => f.Severity == Severity.Error))
            throw new AssertionException("the fixture was meant to produce lint only");
    }

    private static IReadOnlyList<Finding> Inspect(string? materials = null, string? reactions = null) =>
        Validation.InspectZip(Fixture(materials: materials, reactions: reactions));

    private static Finding Expect(IReadOnlyList<Finding> findings, Rule rule, Severity severity, string mentions)
    {
        var matching = findings.Where(f => f.Rule == rule).ToList();
        if (matching.Count == 0)
            throw new AssertionException(
                $"expected a {rule} finding. Got: " + (findings.Count == 0 ? "none"
                    : "\n  " + string.Join("\n  ", findings.Select(f => f.ToString()))));

        if (matching.Count > 1)
            throw new AssertionException(
                $"expected one {rule} finding, got {matching.Count}:\n  " +
                string.Join("\n  ", matching.Select(f => f.ToString())));

        var finding = matching[0];
        if (finding.Severity != severity)
            throw new AssertionException(
                $"{rule} should be {severity}, was {finding.Severity}. A rule that cannot tell " +
                "a deliberate choice from a mistake belongs at Lint, which never fails a check.");

        if (!finding.Message.Contains(mentions))
            throw new AssertionException(
                $"{rule}'s message should name '{mentions}' so it is actionable. Got: {finding.Message}");

        return finding;
    }

    /// <summary>
    /// Writes a one-module mod zip to a scratch path, laid out the way the loader requires:
    /// everything under a top-level folder named exactly the mod id.
    /// </summary>
    private static string Fixture(
        string? materials = null,
        string? reactions = null,
        string materialsPath = "Data/Materials/Materials.json",
        string reactionsPath = "Data/Reactions/Reactions.json")
    {
        const string id = "TestHarness.ValidationFixture";
        // Outside both Mods directories on purpose: a fixture with deliberate faults must
        // never be discoverable by the loader.
        var dir = OS.GetUserDataDir().PathJoin("atomtest-fixtures");
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var path = dir.PathJoin($"{id}-{materials?.Length ?? 0}-{reactions?.Length ?? 0}-{materialsPath.Length}.zip");

        var manifest = $$"""
            {
              "id": "{{id}}",
              "name": "Validation Fixture",
              "modules": [{
                "moduleId": "{{id}}/Main",
                "materials": "{{materialsPath}}",
                "reactions": "{{reactionsPath}}"
              }]
            }
            """;

        using var packer = new ZipPacker();
        if (packer.Open(path) != Error.Ok)
            throw new AssertionException($"could not write a fixture zip to {path}");

        Write(packer, $"{id}/mod.json", manifest);
        if (materials != null) Write(packer, $"{id}/Data/Materials/Materials.json", materials);
        if (reactions != null) Write(packer, $"{id}/Data/Reactions/Reactions.json", reactions);
        packer.Close();

        return path;
    }

    private static void Write(ZipPacker packer, string entry, string content)
    {
        packer.StartFile(entry);
        packer.WriteFile(System.Text.Encoding.UTF8.GetBytes(content));
        packer.CloseFile();
    }
}
