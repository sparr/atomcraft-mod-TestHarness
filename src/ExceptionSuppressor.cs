using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Godot;
using HarmonyLib;

namespace Atomcraft.TestHarness;

/// <summary>
/// Deduplicates engine-level exception logging.
///
/// Godot routes every exception escaping a C# override through
/// <c>Godot.NativeInterop.ExceptionUtils.LogException</c>, once per occurrence, with no
/// backpressure. A single throwing per-frame method produced a 1.3 million line godot.log
/// in under 90 seconds during Phase 1. This prefixes that method so each distinct signature
/// is printed once in full and thereafter only counted.
///
/// Suppression is never silent: counts are reported, and a run that provoked engine
/// exceptions is not a clean run. The count alone is close to meaningless, so what is
/// recorded is the signature, the first full stack, the owning context, and the count
/// relative to frames elapsed, which is what distinguishes a one-off from a per-frame storm.
/// </summary>
public static class ExceptionSuppressor
{
    /// <summary>Frames of identical spam past which the run is aborted rather than continued.</summary>
    public const int AbortThreshold = 2000;

    /// <summary>Exit code for a run killed by an exception storm.</summary>
    public const int StormExitCode = 71;

    public sealed class Entry
    {
        public required string Signature { get; init; }
        public required string Context { get; init; }
        public required int FirstFrame { get; init; }
        public int Count;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();

    /// <summary>
    /// Wall clock since Initialize. Frames alone are not a usable denominator: a storm that
    /// starts inside Game._Ready happens before the pump can attach, so frames is still 0
    /// at exactly the moment the storm/blip ratio matters most.
    /// </summary>
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static int _totalSuppressed;
    private static bool _installed;
    private static bool _aborting;

    [ThreadStatic] private static bool _reentrant;

    /// <summary>
    /// What the harness was doing when an exception was seen. Phase 2 sets this to the
    /// running test's name; until then it distinguishes boot from run, which is already
    /// enough to tell "the game was broken before we started" from "we broke it".
    /// </summary>
    public static string Context { get; set; } = "boot";

    public static bool Any => !Entries.IsEmpty;
    public static int TotalSuppressed => _totalSuppressed;

    /// <summary>Distinct signatures seen. Used to attribute exceptions to the running test.</summary>
    public static int TotalDistinct => Entries.Count;

    public static void Install(Harmony harmony)
    {
        // GodotSharp internals are not nameable from C# and are not part of any stable
        // contract, so failure to find them degrades to "no throttling" rather than to a
        // failed boot.
        try
        {
            var type = AccessTools.TypeByName("Godot.NativeInterop.ExceptionUtils");
            if (type == null)
            {
                Log.Warn("ExceptionUtils not found; engine exception throttling disabled");
                return;
            }

            var prefix = new HarmonyMethod(typeof(ExceptionSuppressor), nameof(LogPrefix));
            var patched = 0;
            foreach (var name in new[] { "LogException", "LogUnhandledException" })
            {
                var method = AccessTools.Method(type, name, new[] { typeof(Exception) });
                if (method == null)
                {
                    Log.Warn($"ExceptionUtils.{name}(Exception) not found; not throttled");
                    continue;
                }
                harmony.Patch(method, prefix: prefix);
                patched++;
            }

            _installed = patched > 0;
            Log.Info($"engine exception throttling {(_installed ? "installed" : "unavailable")} ({patched} methods)");
        }
        catch (Exception ex)
        {
            Log.Warn($"could not install engine exception throttling: {ex.Message}");
        }
    }

    /// <returns>false to skip Godot's own logging of this exception.</returns>
    private static bool LogPrefix(Exception __0)
    {
        // Anything thrown in here would be logged through the very method being patched.
        if (_reentrant)
            return true;

        try
        {
            _reentrant = true;
            return Observe(__0);
        }
        catch
        {
            return true;
        }
        finally
        {
            _reentrant = false;
        }
    }

    private static bool Observe(Exception e)
    {
        if (e == null)
            return true;

        var signature = Signature(e);
        var first = false;

        var entry = Entries.GetOrAdd(signature, sig =>
        {
            first = true;
            return new Entry { Signature = sig, Context = Context, FirstFrame = Runner.Frame };
        });

        Interlocked.Increment(ref entry.Count);

        if (first)
        {
            // Let Godot print this one in full: one complete stack per signature is the
            // diagnostic, and everything after it is noise.
            Log.Warn($"engine exception (first of its kind, context '{entry.Context}'): {signature}");
            return true;
        }

        var total = Interlocked.Increment(ref _totalSuppressed);
        if (total >= AbortThreshold && !_aborting)
        {
            _aborting = true;
            Runner.AbortFromExceptionStorm();
        }
        return false;
    }

    /// <summary>Exception type plus the top few frames: stable enough to group, specific enough to locate.</summary>
    private static string Signature(Exception e)
    {
        var sb = new StringBuilder(e.GetType().Name);
        var trace = new System.Diagnostics.StackTrace(e, fNeedFileInfo: false);
        var shown = 0;
        for (var i = 0; i < trace.FrameCount && shown < 3; i++)
        {
            var method = trace.GetFrame(i)?.GetMethod();
            if (method == null)
                continue;
            sb.Append(shown == 0 ? '@' : '<').Append(Describe(method));
            shown++;
        }
        return sb.ToString();
    }

    private static string Describe(MethodBase m) =>
        $"{m.DeclaringType?.Name ?? "?"}.{m.Name}";

    private static double Rate(int count)
    {
        var seconds = Clock.Elapsed.TotalSeconds;
        return seconds <= 0 ? 0 : Math.Round(count / seconds, 1);
    }

    /// <summary>Records emitted at run end, or when a storm aborts the run.</summary>
    public static List<Dictionary<string, object?>> Summary(int framesElapsed) =>
        Entries.Values
            .OrderByDescending(e => e.Count)
            .Select(e => new Dictionary<string, object?>
            {
                ["sig"] = e.Signature,
                ["context"] = e.Context,
                ["count"] = e.Count,
                ["firstFrame"] = e.FirstFrame,
                ["frames"] = framesElapsed,
                ["elapsedMs"] = (int)Clock.ElapsedMilliseconds,
                ["perSecond"] = Rate(e.Count),
            })
            .ToList();
}
