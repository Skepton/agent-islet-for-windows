namespace Island.Core;

/// <summary>Writes a file so readers see either the old or the new content, never half of it.</summary>
public static class AtomicFile
{
    private const int Attempts = 5;

    /// <summary>
    /// Writes to a temp file next to <paramref name="path"/>, then swaps it in. Windows refuses the swap while
    /// something else briefly has the target open (antivirus or the indexer scanning a file we just wrote), so the
    /// swap is retried a few times before giving up with the last <see cref="IOException"/>.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
                else File.Move(temp, path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
