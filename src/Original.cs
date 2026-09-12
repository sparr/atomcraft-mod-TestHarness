using System.Reflection;
using HarmonyLib;

namespace Atomcraft.TestHarness;

/// <summary>
/// Calling a method as it was before anyone patched it.
///
/// A mod that changes what a game method returns usually wants to compare against what it
/// would have returned. The obvious way is to remove the patch, run, and put it back, which
/// mutates global state, races the parallel simulation, and leaves the game half-patched if the
/// restore is skipped. None of that is necessary for a comparison: Harmony can copy a method's
/// original IL into a stub you supply, so the unpatched code stays callable while the real
/// method stays patched for everyone else.
///
/// The stub is written by the caller because its signature is the contract. Declare a static
/// method matching the target, give it a throwing body so a missed binding is loud, and bind it:
///
///     private static int RollOriginal(int posX, int posY, int tick) =>
///         throw new NotImplementedException("not bound");
///
///     Original.Bind(typeof(RNG), nameof(RNG.Roll),
///         AccessTools.Method(typeof(MyTests), nameof(RollOriginal)),
///         new[] { typeof(int), typeof(int), typeof(int) });
///
/// After that, RollOriginal runs the game's code with no patches applied, and RNG.Roll runs
/// with them. What this does not give you is a whole scenario running unpatched: the simulation
/// calls the patched method from inside its own code, and a delegate you hold cannot redirect
/// that.
/// </summary>
public static class Original
{
    private static Harmony? _harmony;
    private static readonly HashSet<MethodBase> Bound = new();

    internal static void Install(Harmony harmony) => _harmony = harmony;

    /// <summary>
    /// Binds a stub to a target resolved by name. The parameter types disambiguate an
    /// overload, which matters more here than usual: the game overloads heavily, and binding
    /// the wrong overload produces a stub that compiles, runs, and answers a different
    /// question.
    /// </summary>
    public static void Bind(Type declaringType, string methodName, MethodInfo stub,
        Type[]? parameterTypes = null)
    {
        var target = AccessTools.Method(declaringType, methodName, parameterTypes)
            ?? throw new AssertionException(
                $"no method {declaringType.Name}.{methodName}" +
                (parameterTypes == null ? "" : $"({string.Join(", ", parameterTypes.Select(t => t.Name))})") +
                " to bind an original for");

        Bind(target, stub);
    }

    /// <summary>Binds a stub to a target already in hand.</summary>
    public static void Bind(MethodBase target, MethodInfo stub)
    {
        var harmony = _harmony
            ?? throw new AssertionException(
                "Original was never given a Harmony instance; ModEntry.Initialize should call " +
                "Original.Install");

        if (!stub.IsStatic)
            throw new AssertionException(
                $"the stub {stub.Name} must be static: Harmony copies the original's IL into it, " +
                "and an instance method has a receiver the original does not expect");

        Check(target, stub);

        lock (Bound)
        {
            if (!Bound.Add(target))
                throw new AssertionException(
                    $"{target.DeclaringType?.Name}.{target.Name} already has an original bound. " +
                    "Binding twice would copy the IL into a second stub, and which one reflects " +
                    "the original after that is not worth reasoning about.");
        }

        harmony.CreateReversePatcher(target, new HarmonyMethod(stub)).Patch();
        Log.Info($"bound an original for {target.DeclaringType?.Name}.{target.Name} " +
                 $"into {stub.DeclaringType?.Name}.{stub.Name}");
    }

    /// <summary>
    /// Signature agreement, checked here rather than left to Harmony.
    ///
    /// A mismatch otherwise surfaces as a stub that runs and returns something meaningless, or
    /// as a crash from inside copied IL, neither of which names the actual mistake.
    /// </summary>
    private static void Check(MethodBase target, MethodInfo stub)
    {
        var wanted = target.GetParameters().Select(p => p.ParameterType).ToArray();
        var got = stub.GetParameters().Select(p => p.ParameterType).ToArray();

        // An instance method's original takes the receiver as its first argument.
        if (!target.IsStatic)
            wanted = new[] { target.DeclaringType! }.Concat(wanted).ToArray();

        if (!wanted.SequenceEqual(got))
            throw new AssertionException(
                $"stub {stub.Name}({string.Join(", ", got.Select(t => t.Name))}) does not match " +
                $"{target.DeclaringType?.Name}.{target.Name}" +
                $"({string.Join(", ", wanted.Select(t => t.Name))})");

        var returns = (target as MethodInfo)?.ReturnType ?? typeof(void);
        if (returns != stub.ReturnType)
            throw new AssertionException(
                $"stub {stub.Name} returns {stub.ReturnType.Name}, the original returns {returns.Name}");
    }
}
