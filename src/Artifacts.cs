using Godot;

namespace Atomcraft.TestHarness;

/// <summary>
/// A blessed place for a test to write files: a rendering, a dump, a CSV of what happened.
///
/// Without one, every test that produces output invents its own path and its own cleanup, and
/// the paths land wherever the game's working directory happens to be. Worse, output from one
/// run is indistinguishable from output from the last, so a stale file reads as this run's
/// evidence.
///
/// The contract is three things. The directory is emptied at the start of every run, so
/// anything in it belongs to the run you just did. Every file written is announced in the
/// results stream, so a collector knows what to pick up without scanning. And the run's
/// artifacts are copied out of the game's prefix next to the log and the results, so
/// collecting them does not require knowing where the prefix is.
///
/// Files are namespaced by test, so two tests may both write "before.txt" without colliding.
/// </summary>
public static class Artifacts
{
    /// <summary>
    /// Under user:// rather than beside the results, because the mod runs inside the game and
    /// under Proton the results directory is on the far side of a drive mapping. run-game.sh
    /// copies this out afterwards, the same way it collects godot.log.
    /// </summary>
    private const string Root = "user://atomtest-artifacts";

    /// <summary>The run's artifact directory as a real filesystem path.</summary>
    public static string Directory => ProjectSettings.GlobalizePath(Root);

    /// <summary>How many files this run has written. Reported in the run summary.</summary>
    public static int Count { get; private set; }

    /// <summary>
    /// Empties the directory. Called once when a run begins, so a file left by the previous
    /// run can never be mistaken for evidence from this one.
    /// </summary>
    public static void Reset()
    {
        Count = 0;

        var dir = Directory;
        if (System.IO.Directory.Exists(dir))
        {
            try
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
            catch (Exception e)
            {
                // Not fatal: a run with stale artifacts is worth having, as long as the stale
                // ones are not silently presented as fresh.
                Log.Warn($"could not clear the artifact directory: {e.Message}. " +
                         "Files from an earlier run may still be present.");
            }
        }

        System.IO.Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// A path to write to, inside the running test's own subdirectory, which is created if
    /// needed. Use this when something else does the writing, a renderer or an encoder; use
    /// Write or WriteBytes when the content is already in hand, since those also announce the
    /// file.
    ///
    /// Call Announce yourself after writing through this, or the file will be collected but
    /// not reported.
    /// </summary>
    public static string Path(string name)
    {
        var test = TestExecutor.CurrentTestName ?? "run";
        var dir = System.IO.Path.Combine(Directory, Sanitize(test));
        System.IO.Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, Sanitize(name));
    }

    /// <summary>Writes a text file and announces it. Returns the path.</summary>
    public static string Write(string name, string content)
    {
        var path = Path(name);
        System.IO.File.WriteAllText(path, content);
        Announce(path);
        return path;
    }

    /// <summary>Writes a binary file and announces it. Returns the path.</summary>
    public static string WriteBytes(string name, byte[] content)
    {
        var path = Path(name);
        System.IO.File.WriteAllBytes(path, content);
        Announce(path);
        return path;
    }

    /// <summary>
    /// Records a file in the results stream. A collector reads these rather than walking the
    /// directory, so an artifact written and then overwritten or removed is still accounted
    /// for, and so the test that produced it is named.
    /// </summary>
    public static void Announce(string path)
    {
        Count++;

        var size = System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : -1;

        Log.Event("artifact", new()
        {
            ["test"] = TestExecutor.CurrentTestName,
            // Relative, because the absolute path is inside a Wine prefix and means nothing
            // to whoever collects the copied-out directory.
            ["file"] = System.IO.Path.GetRelativePath(Directory, path).Replace('\\', '/'),
            ["bytes"] = (int)size,
        });
    }

    /// <summary>
    /// Keeps a test name or a caller's filename usable as a path component. Test names carry
    /// dots and nothing else troublesome, but a caller's name is arbitrary.
    /// </summary>
    private static string Sanitize(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }
}
