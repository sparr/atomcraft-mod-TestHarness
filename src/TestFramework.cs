using System.Reflection;

namespace Atomcraft.TestHarness;

/// <summary>Thrown by harness assertions. Any exception escaping a test fails it.</summary>
public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}

/// <summary>
/// Marks a test that runs inside the game. The method must be static. It may take no
/// parameters, or a single <see cref="Region"/>, which the runner allocates and clears
/// beforehand.
///
/// It may return void, or <see cref="System.Collections.IEnumerator"/> to run across engine
/// frames: yield a <see cref="Wait"/> to pause until the game has made progress. Anything
/// involving a session, a world load, or a save has to yield, because the game only
/// advances between frames.
///
/// Named for where it runs, not for what it does. Plenty of these never step the
/// simulation at all: checking that a material is registered, or that a Harmony postfix
/// installed a custom class, needs the game loaded but no ticks. What they have in common
/// is the game, which is also what separates them from ordinary unit tests run under
/// dotnet test.
///
/// Not [Test], which would collide with NUnit's for any consumer importing both namespaces.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GameTestAttribute : Attribute
{
    /// <summary>
    /// Region size in 64x64 chunks, the engine's own unit of work. One chunk is plenty for
    /// most pixel-behavior tests, and the unused remainder is air, which doubles as padding.
    /// </summary>
    public int ChunksWide { get; set; } = 1;
    public int ChunksTall { get; set; } = 1;

    /// <summary>Which altitude band the region sits in. Absolute Y changes behavior.</summary>
    public Altitude Band { get; set; } = Altitude.Deep;

    /// <summary>Absolute tick the region starts at.</summary>
    public int StartTick { get; set; }

    /// <summary>
    /// Seal the region with a wall of this material before the test runs, so liquid and gas
    /// tests need not build a container. Must be a static material.
    /// </summary>
    public string? Wall { get; set; }

    /// <summary>Wall thickness in cells, when <see cref="Wall"/> is set.</summary>
    public int WallThickness { get; set; } = 1;

    /// <summary>
    /// World mode for this test. Plenty of game logic branches on it: Game.IsFree,
    /// IsDigestionMechanicInPlay, and IsJetpackMeterMechanicInPlay all read it, so a mod
    /// whose behavior differs between creative and survival needs to say which it means.
    /// </summary>
    public WorldMode Mode { get; set; } = WorldMode.Creative;

    /// <summary>
    /// Reset every registered mod channel and every registered piece of global state before
    /// this test. Leave on unless the test deliberately builds on state an earlier one left.
    /// </summary>
    public bool ResetModState { get; set; } = true;

    /// <summary>Skip with a stated reason, rather than deleting or commenting out.</summary>
    public string? Skip { get; set; }

    /// <summary>
    /// Frames a frame-driven test may run before it is failed as hung. Generous by default:
    /// loading a world takes a while, and a test that genuinely needs longer should say so
    /// rather than be cut off.
    /// </summary>
    public int TimeoutFrames { get; set; } = 3000;
}

/// <summary>A discovered test and the outcome of running it.</summary>
public sealed class TestCase
{
    public required MethodInfo Method { get; init; }
    public required GameTestAttribute Attr { get; init; }

    /// <summary>Assembly-qualified so two mods can name a test the same thing.</summary>
    public string Name => $"{Method.DeclaringType?.Assembly.GetName().Name}.{Method.DeclaringType?.Name}.{Method.Name}";

    public string Status { get; set; } = "pending";
    public string? Failure { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Finds tests in every loaded mod assembly.
///
/// Scanning all assemblies rather than a fixed list is what lets another mod ship its own
/// test module without the harness knowing anything about it.
/// </summary>
public static class TestDiscovery
{
    public static List<TestCase> Discover(string? filter)
    {
        var found = new List<TestCase>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A mod with a half-resolvable dependency should not stop discovery.
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                catch
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    var attr = method.GetCustomAttribute<GameTestAttribute>();
                    if (attr == null)
                        continue;

                    var test = new TestCase { Method = method, Attr = attr };
                    if (filter != null && !test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    found.Add(test);
                }
            }
        }

        return found.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }
}
