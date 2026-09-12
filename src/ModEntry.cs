using System.Reflection;
using System.Text;
using Atomcraft;
using Godot;
using HarmonyLib;

namespace Atomcraft.TestHarness;

/// <summary>
/// Loader entry point. Runs before <c>Game._Ready</c>, so it may only install Harmony
/// patches and set up harness state: no Materials, Craftables, Reactions, Simulation or
/// Game access from here.
/// </summary>
public static class ModEntry
{
    public const string ModId = "TestHarness";
    /// <summary>
    /// The released version. Between releases this carries the next minor version with a -dev
    /// suffix, so RequireVersion refuses a mod built against the last release as soon as the
    /// API moves rather than at release time; the check compares major and minor, so leaving
    /// it at the released number is how a consumer ends up with a MissingMethodException
    /// instead of a message naming both versions.
    /// </summary>
    public static readonly string Version = "0.3.0";

    /// <summary>Parsed from the args after <c>--</c> on the game command line.</summary>
    public static HarnessOptions Options { get; private set; } = new();

    public static void Initialize()
    {
        Options = HarnessOptions.Parse(OS.GetCmdlineUserArgs());

        Log.Banner($"{ModId} {Version} initializing");
        Log.Event("boot", new()
        {
            ["harness"] = Version,
            ["headlessServer"] = Options.HeadlessServer,
            ["run"] = Options.Run,
        });

        if (Options.HeadlessServer)
        {
            // Skips Cursors, Inventory and EditorHUD node init in Game._Ready, and
            // suppresses client feedback in Simulation. Never set by the shipped game.
            Game.IsHeadlessServer = true;
            Log.Info("Game.IsHeadlessServer = true");
        }

        AssemblyDiagnostics.WarnOnDuplicateSimpleNames();

        var harmony = new Harmony("sparr." + ModId);
        ExceptionSuppressor.Install(harmony);
        WorldFixtures.Install(harmony);
        SimFeatures.Install(harmony);
        Original.Install(harmony);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        foreach (var m in harmony.GetPatchedMethods())
            Log.Info($"patched: {m.DeclaringType?.FullName}.{m.Name}");
    }

}

/// <summary>Command line options, all namespaced to avoid colliding with other mods.</summary>
public sealed class HarnessOptions
{
    /// <summary>Run the suite and quit. Without this the harness stays passive.</summary>
    public bool Run { get; private set; }

    /// <summary>
    /// Set <see cref="Game.IsHeadlessServer"/> before the game initializes. Defaults OFF:
    /// the flag is dead code in the shipped game and enabling it throws a
    /// NullReferenceException every frame (see PLAN.md section 8.3). Kept as an opt-in only
    /// so a future game version that wires it up can be retested cheaply.
    /// </summary>
    public bool HeadlessServer { get; private set; }

    /// <summary>Frames to wait after boot before running. Boot is ~6.5s, most of it pre-frame.</summary>
    public int SettleFrames { get; private set; } = 2;

    /// <summary>Forced exit code, for proving the wrapper propagates failures.</summary>
    public int? ForceExitCode { get; private set; }

    /// <summary>Do not fail the run merely because the engine logged exceptions.</summary>
    public bool AllowEngineExceptions { get; private set; }

    /// <summary>Substring match against the fully qualified test name.</summary>
    public string? Filter { get; private set; }

    /// <summary>
    /// Tests to leave out, as a regular expression. Applied after Filter, so the two compose:
    /// select a class and drop one test from it. Expressing that with Filter alone needs a
    /// negative lookahead, which nobody enjoys reading or writing.
    /// </summary>
    public string? Exclude { get; private set; }

    /// <summary>
    /// Run benchmarks instead of tests. They are a measurement rather than a verdict, so they
    /// are not mixed into an ordinary run.
    /// </summary>
    public bool Benchmarks { get; private set; }

    /// <summary>Run the one-shot game diagnostics before the suite.</summary>
    public bool Diagnose { get; private set; }

    public static HarnessOptions Parse(string[] args)
    {
        var o = new HarnessOptions();
        foreach (var arg in args)
        {
            var (key, value) = Split(arg);
            switch (key)
            {
                case "--atomtest-run":             o.Run = true; break;
                case "--atomtest-headless-server": o.HeadlessServer = value != "false"; break;
                case "--atomtest-settle-frames":   o.SettleFrames = ParseInt(value, o.SettleFrames); break;
                case "--atomtest-force-exit":      o.ForceExitCode = ParseInt(value, 0); break;
                case "--atomtest-allow-engine-exceptions": o.AllowEngineExceptions = true; break;
                case "--atomtest-filter":          o.Filter = value; break;
                case "--atomtest-exclude":         o.Exclude = value; break;
                case "--atomtest-bench":           o.Benchmarks = true; break;
                case "--atomtest-diagnose":        o.Diagnose = true; break;
            }
        }
        return o;
    }

    private static (string, string?) Split(string arg)
    {
        var i = arg.IndexOf('=');
        return i < 0 ? (arg, null) : (arg[..i], arg[(i + 1)..]);
    }

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var v) ? v : fallback;
}

/// <summary>
/// Detects the one collision the shared AssemblyLoadContext can produce: two mods shipping
/// different builds of the same library, where first-load silently wins.
/// </summary>
public static class AssemblyDiagnostics
{
    public static void WarnOnDuplicateSimpleNames()
    {
        var groups = AppDomain.CurrentDomain.GetAssemblies()
            .GroupBy(a => a.GetName().Name)
            .Where(g => g.Select(a => a.GetName().Version?.ToString() ?? "?").Distinct().Count() > 1);

        foreach (var g in groups)
        {
            var detail = string.Join(", ", g.Select(a => $"{a.GetName().Version} <{Location(a)}>"));
            Log.Warn($"assembly '{g.Key}' loaded more than once with differing versions: {detail}. "
                   + "First load wins; the others are inert.");
        }
    }

    private static string Location(Assembly a)
    {
        try { return string.IsNullOrEmpty(a.Location) ? "dynamic" : a.Location; }
        catch { return "unknown"; }
    }
}

/// <summary>
/// All harness output. Machine-readable records are single-line JSON prefixed with a
/// stable marker so a wrapper can extract them from godot.log without parsing prose.
/// </summary>
/// <summary>
/// A log bound to one mod's name, so a line in godot.log can be attributed to whoever wrote it.
///
/// With several mods in a run, everything going out under the harness's own name is
/// unreadable, and a mod that works around it by calling GD.Print directly loses the marker
/// conventions that make a record machine-readable.
/// </summary>
public sealed class ModLog
{
    private readonly string _id;

    internal ModLog(string id) => _id = id;

    public void Banner(string message) => GD.Print($"[{_id}] === {message} ===");
    public void Info(string message)   => GD.Print($"[{_id}] {message}");
    public void Warn(string message)   => GD.Print($"[{_id}] WARNING: {message}");
    public void Error(string message)  => GD.PrintErr($"[{_id}] ERROR: {message}");

    /// <summary>
    /// A structured record, carrying the mod that wrote it so a reader can tell records apart
    /// without parsing prose.
    /// </summary>
    public void Event(string kind, Dictionary<string, object?> fields)
    {
        var withSource = new Dictionary<string, object?>(fields);
        withSource["mod"] = _id;
        Log.Event(kind, withSource);
    }
}

public static class Log
{
    public const string Marker = "##ATOMTEST##";

    /// <summary>
    /// A log that writes under your mod's name instead of the harness's.
    ///
    ///     private static readonly ModLog Log = Atomcraft.TestHarness.Log.For("MyMod");
    ///
    /// Taken as an argument rather than inferred from the calling assembly: inference means
    /// another stack walk, and the one place the harness already guesses a caller that way is
    /// only safe because it merely picks the wording of a message.
    /// </summary>
    public static ModLog For(string modId) => new(modId);

    public static void Banner(string message) => GD.Print($"[{ModEntry.ModId}] === {message} ===");
    public static void Info(string message)   => GD.Print($"[{ModEntry.ModId}] {message}");
    public static void Warn(string message)   => GD.Print($"[{ModEntry.ModId}] WARNING: {message}");
    public static void Error(string message)  => GD.PrintErr($"[{ModEntry.ModId}] ERROR: {message}");

    public static void Event(string kind, Dictionary<string, object?> fields)
    {
        var sb = new StringBuilder(Marker).Append(" {\"event\":").Append(Json(kind));
        foreach (var (k, v) in fields)
        {
            // "event" is the record kind. A field of the same name produces a duplicate key
            // that a strict parser resolves by silently dropping one of them.
            if (k == "event")
            {
                Warn($"ignoring a field named 'event' in a '{kind}' record; rename it");
                continue;
            }
            sb.Append(',').Append(Json(k)).Append(':').Append(Json(v));
        }
        GD.Print(sb.Append('}').ToString());
    }

    private static string Json(object? value) => value switch
    {
        null        => "null",
        bool b      => b ? "true" : "false",
        int i       => i.ToString(),
        long l      => l.ToString(),
        double d    => d.ToString("R"),
        float f     => f.ToString("R"),
        _           => Quote(value.ToString() ?? ""),
    };

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
            sb.Append(c switch
            {
                '"'  => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _    => c < ' ' ? $"\\u{(int)c:x4}" : c.ToString(),
            });
        return sb.Append('"').ToString();
    }
}
