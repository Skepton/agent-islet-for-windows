namespace Island.Core;

public static class TextUtil
{
    /// <summary>Collapses all whitespace (including newlines) and cuts to <paramref name="max"/> characters, ending with an ellipsis when cut.</summary>
    public static string Truncate(string text, int max)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= max ? collapsed : collapsed[..Math.Max(0, max - 1)] + "…";
    }
}
