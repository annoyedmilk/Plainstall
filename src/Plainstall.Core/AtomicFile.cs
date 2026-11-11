namespace Plainstall.Core;

/// <summary>
///     Writes a file so that a reader never sees half of it: the content goes to a temp file next to the
///     target, which then replaces the target in one step.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    ///     The replace step can fail for a moment when a virus scanner or the search indexer has just opened
    ///     the file, so it is retried briefly before the error is passed on.
    /// </summary>
    public static void WriteAllText(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        const int attempts = 8;
        for (var attempt = 1;; attempt++)
            try
            {
                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25 * attempt);
            }
    }
}