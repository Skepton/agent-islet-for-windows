namespace Island.Core;

public static class TextUtil
{
    /// <summary>Collapses all whitespace (including newlines) and cuts to <paramref name="max"/> characters, ending with an ellipsis when cut.</summary>
    public static string Truncate(string text, int max)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= max ? collapsed : collapsed[..Math.Max(0, max - 1)] + "…";
    }

    /// <summary>
    /// For card bodies: keeps line breaks (at most one blank line in a row), collapses other whitespace within each
    /// line, and cuts to <paramref name="max"/> characters with an ellipsis.
    /// </summary>
    public static string TruncateBlock(string text, int max)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (kept.Count == 0 || kept[^1].Length == 0)) continue; // no leading or doubled blank lines
            kept.Add(line);
        }
        while (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);

        var block = string.Join('\n', kept);
        return block.Length <= max ? block : block[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }
}
