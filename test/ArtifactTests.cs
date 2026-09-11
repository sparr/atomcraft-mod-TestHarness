namespace Atomcraft.TestHarness.Test;

/// <summary>
/// The artifact directory: one blessed place for a test's output, emptied per run and
/// announced in the results so a collector can pick things up before the next run clears them.
/// </summary>
public static class ArtifactTests
{
    [GameTest]
    public static void AWrittenArtifactLandsUnderTheRunningTest()
    {
        var path = Artifacts.Write("note.txt", "hello");

        if (!System.IO.File.Exists(path))
            throw new AssertionException($"nothing was written at {path}");

        if (System.IO.File.ReadAllText(path) != "hello")
            throw new AssertionException("the file does not hold what was written");

        // Filed under the test, so two tests can both write "note.txt".
        var dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path));
        if (dir == null || !dir.Contains(nameof(AWrittenArtifactLandsUnderTheRunningTest)))
            throw new AssertionException(
                $"expected the file under a directory named for the test, got '{dir}'");
    }

    /// <summary>
    /// Two tests writing the same filename must not collide, which is the whole reason for
    /// namespacing by test rather than handing out a flat directory.
    /// </summary>
    [GameTest]
    public static void AnotherTestCanUseTheSameFilename()
    {
        var path = Artifacts.Write("note.txt", "different");

        if (System.IO.File.ReadAllText(path) != "different")
            throw new AssertionException("this test's file was overwritten by another test's");
    }

    [GameTest]
    public static void BinaryArtifactsRoundTrip()
    {
        var bytes = new byte[] { 0, 1, 2, 250, 255 };
        var path = Artifacts.WriteBytes("blob.bin", bytes);

        if (!System.IO.File.ReadAllBytes(path).SequenceEqual(bytes))
            throw new AssertionException("the bytes read back do not match");
    }

    /// <summary>
    /// Path hands out a location without writing, for a caller that produces the file some
    /// other way. It must create the directory, or that caller fails on a missing path.
    /// </summary>
    [GameTest]
    public static void PathCreatesTheDirectoryWithoutWriting()
    {
        var path = Artifacts.Path("rendered.png");
        var dir = System.IO.Path.GetDirectoryName(path)!;

        if (!System.IO.Directory.Exists(dir))
            throw new AssertionException($"the directory was not created: {dir}");

        if (System.IO.File.Exists(path))
            throw new AssertionException("Path should not write anything");
    }

    /// <summary>
    /// Writing announces, so a collector reads the results stream rather than walking the
    /// tree. Without this the count could stay at zero and everything else would still pass.
    /// </summary>
    [GameTest]
    public static void WritingAnArtifactIsCounted()
    {
        var before = Artifacts.Count;
        Artifacts.Write("counted.txt", "x");

        if (Artifacts.Count != before + 1)
            throw new AssertionException(
                $"the artifact count went {before} -> {Artifacts.Count}; a collector reads " +
                "these announcements to know what to pick up");
    }

    /// <summary>
    /// A name that is not a legal filename must not escape the directory or throw. Test names
    /// are tame; a caller's filename is arbitrary.
    /// </summary>
    [GameTest]
    public static void AnAwkwardNameIsMadeSafe()
    {
        var path = Artifacts.Write("../escaped/../../note:1.txt", "safe");
        var full = System.IO.Path.GetFullPath(path);

        if (!full.StartsWith(System.IO.Path.GetFullPath(Artifacts.Directory)))
            throw new AssertionException($"the file escaped the artifact directory: {full}");
    }
}
