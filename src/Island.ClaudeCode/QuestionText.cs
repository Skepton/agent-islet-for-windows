using System.Text;
using System.Text.Json;
using Island.Core;

namespace Island.ClaudeCode;

/// <summary>
/// The card text for an AskUserQuestion call: each question followed by its options, e.g.
/// "What should I do next?\n• Build it\n• Commit first". Tolerant: anything unexpected is skipped.
/// </summary>
public static class QuestionText
{
    private const int MaxLength = 400;

    /// <returns>Null when the input has no readable question.</returns>
    public static string? From(string? toolInputJson)
    {
        if (string.IsNullOrEmpty(toolInputJson)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(toolInputJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (JsonUtil.Prop(doc.RootElement, "questions") is not { ValueKind: JsonValueKind.Array } questions) return null;

            var text = new StringBuilder();
            foreach (var question in questions.EnumerateArray())
            {
                if (JsonUtil.Str(question, "question") is not { Length: > 0 } asked) continue;
                if (text.Length > 0) text.Append("\n\n");
                text.Append(asked.Trim());

                if (JsonUtil.Prop(question, "options") is not { ValueKind: JsonValueKind.Array } options) continue;
                foreach (var option in options.EnumerateArray())
                {
                    if (JsonUtil.Str(option, "label") is { Length: > 0 } label) text.Append("\n• ").Append(label.Trim());
                }
            }

            if (text.Length == 0) return null;
            return text.Length <= MaxLength ? text.ToString() : text.ToString(0, MaxLength - 1) + "…";
        }
    }
}
