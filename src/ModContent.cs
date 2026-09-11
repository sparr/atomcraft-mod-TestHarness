using System.Reflection;
using Godot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Atomcraft.TestHarness;

/// <summary>
/// What one mod ships, read back out of its own zip.
///
/// The loader flattens every mod's materials and reactions into two shared static lists, so
/// asking it what a particular mod contributed is not possible after the fact. Reading the
/// zip again is both exact and immune to load order, which matters because a mod's data may
/// well have been read before the harness itself was loaded.
///
/// Reading the raw JSON also preserves names. Once the game has resolved a material
/// reference it is a short, and a name that failed to resolve is indistinguishable from a
/// deliberate -1, which is the whole thing validation needs to see.
/// </summary>
public sealed class ModContent
{
    public string ModId { get; private init; } = "";
    public string ZipPath { get; private init; } = "";

    /// <summary>Materials declared in the mod's JSON, unresolved, exactly as shipped.</summary>
    public List<Serializable_MaterialType> Materials { get; } = [];

    /// <summary>Reactions declared in the mod's JSON, unresolved.</summary>
    public List<ReactionType> Reactions { get; } = [];

    /// <summary>Every translation key the mod defines, across all locales.</summary>
    public HashSet<string> TranslationKeys { get; } = [];

    /// <summary>Assembly names from the manifest's modules, without the .dll suffix.</summary>
    public List<string> AssemblyNames { get; } = [];

    /// <summary>
    /// Manifest data paths that matched nothing in the zip, as (moduleId, field, path). The
    /// loader is silent about these: it loads no data and reports no error.
    /// </summary>
    public List<(string ModuleId, string Field, string Path)> EmptyDeclaredPaths { get; } = [];

    /// <summary>
    /// The same two directories the loader scans, in the same order. Duplicated rather than
    /// asked of the loader because the loader keeps no list of where a mod came from.
    /// </summary>
    public static IEnumerable<string> ModDirectories()
    {
        yield return OS.GetExecutablePath().GetBaseDir().PathJoin("Mods");
        yield return OS.GetUserDataDir().PathJoin("Mods");
    }

    /// <summary>Every installed mod id, for diagnostics and for validating a whole install.</summary>
    public static IEnumerable<string> InstalledModIds()
    {
        foreach (var zip in Zips())
        {
            var id = IdOf(zip);
            if (id != null) yield return id;
        }
    }

    /// <summary>
    /// Reads one zip directly, whether or not it is installed. A mod that crashes the game at
    /// load cannot be inspected any other way, and that is exactly when validation is most
    /// useful; it also lets a CI job check a freshly built zip before shipping it.
    /// </summary>
    public static ModContent FromZip(string zipPath)
    {
        var id = IdOf(zipPath)
            ?? throw new AssertionException($"'{zipPath}' holds no readable mod.json");

        var content = new ModContent { ModId = id, ZipPath = zipPath };
        content.Read(zipPath);
        return content;
    }

    /// <summary>
    /// Reads one mod's shipped data. Throws if no installed zip declares that id, since a
    /// silent empty result would let every rule pass by inspecting nothing.
    /// </summary>
    public static ModContent Load(string modId)
    {
        foreach (var zip in Zips())
        {
            if (IdOf(zip) != modId) continue;

            var content = new ModContent { ModId = modId, ZipPath = zip };
            content.Read(zip);
            return content;
        }

        var seen = string.Join(", ", InstalledModIds().OrderBy(s => s));
        throw new AssertionException(
            $"no installed mod zip declares the id '{modId}'. Installed: {(seen.Length == 0 ? "none" : seen)}. " +
            $"Searched {string.Join(" and ", ModDirectories())}.");
    }

    private static IEnumerable<string> Zips()
    {
        foreach (var dir in ModDirectories())
        {
            using var da = DirAccess.Open(dir);
            if (da == null) continue;
            foreach (var file in da.GetFiles())
                if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    yield return dir.PathJoin(file);
        }
    }

    private static JObject? Manifest(string zipPath)
    {
        using var reader = new ZipReader();
        if (reader.Open(zipPath) != Error.Ok) return null;
        foreach (var entry in reader.GetFiles())
        {
            if (entry.GetFile() != "mod.json") continue;
            try
            {
                return JObject.Parse(System.Text.Encoding.UTF8.GetString(reader.ReadFile(entry)));
            }
            catch (JsonException)
            {
                return null;   // a malformed manifest is the loader's problem to report
            }
        }
        return null;
    }

    private static string? IdOf(string zipPath) => Manifest(zipPath)?["id"]?.ToString();

    private void Read(string zipPath)
    {
        var manifest = Manifest(zipPath);
        // dllModules is the loader's deprecated alias for modules; a mod using it is still
        // loaded, so validation has to understand it too.
        var modules = manifest?["modules"] as JArray ?? manifest?["dllModules"] as JArray ?? [];

        using var reader = new ZipReader();
        if (reader.Open(zipPath) != Error.Ok) return;

        foreach (var module in modules)
        {
            var moduleId = module["moduleId"]?.ToString() ?? "";
            // Paths in a manifest are relative to the mod's top-level folder, which the
            // loader requires to be named exactly the mod id.
            var root = ModId;

            var dll = module["dll"]?.ToString();
            if (!string.IsNullOrEmpty(dll))
                AssemblyNames.Add(dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? dll[..^4] : dll);

            foreach (var file in Declared(reader, root, moduleId, "materials", module))
                Materials.AddRange(Parse<Serializable_MaterialType>(reader, file, moduleId));

            foreach (var file in Declared(reader, root, moduleId, "reactions", module))
                Reactions.AddRange(Parse<ReactionType>(reader, file, moduleId));

            foreach (var file in Declared(reader, root, moduleId, "translations", module))
                ReadTranslations(reader, file);
        }
    }

    /// <summary>
    /// Resolves one manifest field's path and records it when it matches nothing, since a
    /// path that resolves to no files is indistinguishable from an absent field at load time.
    /// </summary>
    private List<string> Declared(ZipReader reader, string root, string moduleId, string field, JToken module)
    {
        var path = module[field]?.ToString();
        if (string.IsNullOrEmpty(path)) return [];

        var files = Entries(reader, root, path).ToList();
        if (files.Count == 0) EmptyDeclaredPaths.Add((moduleId, field, path));
        return files;
    }

    /// <summary>
    /// A manifest path is either a single .json file or a folder prefix under which every
    /// .json is loaded. Mirrors the loader so validation sees exactly what the game saw.
    /// </summary>
    private static IEnumerable<string> Entries(ZipReader reader, string root, string? path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        var full = root.PathJoin(path);

        if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            yield return full;
            yield break;
        }

        foreach (var entry in reader.GetFiles())
            if (entry.StartsWith(full) && entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                yield return entry;
    }

    private static List<T> Parse<T>(ZipReader reader, string file, string moduleId)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(reader.ReadFile(file));
            return JsonConvert.DeserializeObject<List<T>>(text) ?? [];
        }
        catch (Exception e)
        {
            // The loader logs and continues on a bad data file, so the game is already
            // running without it. Surfacing it here is more useful than inspecting nothing.
            throw new AssertionException(
                $"module '{moduleId}' ships '{file}' but it did not parse as a list of " +
                $"{typeof(T).Name}: {e.Message}");
        }
    }

    private void ReadTranslations(ZipReader reader, string file)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(reader.ReadFile(file));
            var byLocale = JObject.Parse(text);
            foreach (var locale in byLocale.Properties())
                if (locale.Value is JObject messages)
                    foreach (var message in messages.Properties())
                        TranslationKeys.Add(message.Name);
        }
        catch (JsonException)
        {
            // Same reasoning as Parse, but a broken translations file costs only strings.
        }
    }
}
