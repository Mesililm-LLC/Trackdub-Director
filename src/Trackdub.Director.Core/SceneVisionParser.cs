using System.Text.Json;

namespace Trackdub.Director.Core;

/// <summary>
/// Enrichment values parsed from a scene-vision-v1 response. Null means the model
/// reported nothing visible for that field; an empty text array means it looked
/// and found no text of that kind. The two are never collapsed.
/// </summary>
public sealed record VisionEnrichment(
    string? SceneSummary,
    string? EmotionToneLabel,
    string? SpeakerDescription,
    string[]? OnScreenText,
    string[]? BurnedInText);

public sealed record VisionParseResult(bool IsMalformed, VisionEnrichment? Enrichment, string? Error);

/// <summary>
/// Parser for the probe-verified scene-vision-v1 response: one JSON object with
/// exactly the keys scene_summary, emotion_tone_label, speaker_description,
/// on_screen_text, burned_in_text. Prose or fence wrapping is tolerated (the
/// first balanced JSON object is extracted, as in <see cref="MeaningGate"/>),
/// but every key must be present and correctly typed: scalars are string-or-null
/// and text fields are arrays of strings (or null). Anything else is malformed
/// and callers must fail closed with null enrichment fields, never default them.
/// </summary>
public static class SceneVisionParser
{
    public static VisionParseResult Parse(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return Malformed("Vision response was empty.");
        }

        string content = rawContent.Trim();
        string jsonCandidate = ExtractJsonObject(content) ?? content;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonCandidate);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Malformed($"Vision response must be a JSON object: {Truncate(content)}");
            }

            string? error = RequireKeys(root);
            if (error is not null)
            {
                return Malformed(error);
            }

            return new VisionParseResult(
                false,
                new VisionEnrichment(
                    Scalar(root, "scene_summary"),
                    Scalar(root, "emotion_tone_label"),
                    Scalar(root, "speaker_description"),
                    TextArray(root, "on_screen_text"),
                    TextArray(root, "burned_in_text")),
                null);
        }
        catch (JsonException ex)
        {
            return Malformed($"Unparseable vision response ({ex.Message}): {Truncate(content)}");
        }
    }

    private static VisionParseResult Malformed(string error)
    {
        return new VisionParseResult(true, null, error);
    }

    private static string? RequireKeys(JsonElement root)
    {
        // Duplicates are malformed: GetProperty would silently keep the last value.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return $"Vision response contains duplicate key '{property.Name}'.";
            }
        }

        string[] required = ["scene_summary", "emotion_tone_label", "speaker_description", "on_screen_text", "burned_in_text"];
        foreach (string key in required)
        {
            if (!root.TryGetProperty(key, out _))
            {
                return $"Vision response is missing required key '{key}'.";
            }
        }

        return null;
    }

    private static string? Scalar(JsonElement root, string key)
    {
        JsonElement value = root.GetProperty(key);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Vision response '{key}' must be a string or null.");
        }

        string? text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string[]? TextArray(JsonElement root, string key)
    {
        JsonElement value = root.GetProperty(key);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Vision response '{key}' must be an array of strings or null.");
        }

        var result = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Vision response '{key}' elements must be strings.");
            }

            string? text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(text.Trim());
            }
        }

        return result.ToArray();
    }

    // Mirrors MeaningGate's balanced-object scan: braces inside string literals
    // cannot over-extend or truncate the extracted object.
    private static string? ExtractJsonObject(string content)
    {
        int start = content.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < content.Length; i++)
        {
            char c = content[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return content[start..(i + 1)];
                    }

                    break;
            }
        }

        // Unbalanced: let the caller's parser fail closed.
        return null;
    }

    private static string Truncate(string value)
    {
        return value.Length > 200 ? value[..200] : value;
    }
}
