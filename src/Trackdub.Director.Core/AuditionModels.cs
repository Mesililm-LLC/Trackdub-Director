using System.Text.Json.Serialization;

namespace Trackdub.Director.Core;

/// <summary>Input segment for the audition lane: the stamped line plus its character.</summary>
public sealed record AuditionSegmentInput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("duration_seconds")] double? DurationSeconds = null,
    [property: JsonPropertyName("speaker_id")] string? SpeakerId = null);

/// <summary>The stock voice cast for one character, as chosen from the account voice list.</summary>
public sealed record AuditionCastVoice(
    [property: JsonPropertyName("speaker_id")] string SpeakerId,
    [property: JsonPropertyName("voice_id")] string VoiceId,
    [property: JsonPropertyName("voice_name")] string VoiceName);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditionTimingVerdict
{
    Pass,
    Fail,
    NotSynthesized,
}

/// <summary>
/// One candidate take with every verdict preserved, win or lose.
/// <see cref="AudioBytes"/> is transport-only (never serialized): the caller
/// persists winning bytes to files; reports and manifests carry names only.
/// </summary>
public sealed record AuditionTakeReport(
    [property: JsonPropertyName("take_number")] int TakeNumber,
    [property: JsonPropertyName("kind")] QaAttemptKind Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("meaning_verdict")] MeaningVerdict MeaningVerdict,
    [property: JsonPropertyName("meaning_issues")] string[]? MeaningIssues,
    [property: JsonPropertyName("meaning_explanation")] string? MeaningExplanation,
    [property: JsonPropertyName("synthesized")] bool Synthesized,
    [property: JsonPropertyName("measured_seconds")] double? MeasuredSeconds,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("timing_verdict")] AuditionTimingVerdict TimingVerdict,
    [property: JsonPropertyName("is_winner")] bool IsWinner,
    [property: JsonPropertyName("audio_file_name")] string? AudioFileName,
    [property: JsonPropertyName("synthesis_note")] string? SynthesisNote,
    [property: JsonPropertyName("translation_tokens")] int TranslationTokens,
    [property: JsonPropertyName("meaning_tokens")] int MeaningTokens,
    [property: JsonPropertyName("elevenlabs_characters")] int ElevenLabsCharacters,
    [property: JsonIgnore] byte[]? AudioBytes = null);

public sealed record AuditionSegmentReport(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("speaker_id")] string? SpeakerId,
    [property: JsonPropertyName("cast_voice")] AuditionCastVoice? CastVoice,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("takes")] AuditionTakeReport[] Takes,
    [property: JsonPropertyName("cast")] bool Cast,
    [property: JsonPropertyName("winner_take_number")] int? WinnerTakeNumber,
    [property: JsonPropertyName("winner_file_name")] string? WinnerFileName,
    [property: JsonPropertyName("note")] string? Note = null);

public sealed record AuditionTotals(
    [property: JsonPropertyName("total_segments")] int TotalSegments,
    [property: JsonPropertyName("lines_cast")] int LinesCast,
    [property: JsonPropertyName("takes_synthesized")] int TakesSynthesized,
    [property: JsonPropertyName("meta_calls")] int MetaCalls,
    [property: JsonPropertyName("translation_tokens")] int TranslationTokens,
    [property: JsonPropertyName("meaning_tokens")] int MeaningTokens,
    [property: JsonPropertyName("elevenlabs_characters")] int ElevenLabsCharacters,
    [property: JsonPropertyName("elevenlabs_cap_hit")] bool ElevenLabsCapHit,
    [property: JsonPropertyName("meta_cap_hit")] bool MetaCapHit);

public sealed record AuditionReport(
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage,
    [property: JsonPropertyName("cast")] AuditionCastVoice[] Cast,
    [property: JsonPropertyName("segments")] AuditionSegmentReport[] Segments,
    [property: JsonPropertyName("totals")] AuditionTotals Totals);
