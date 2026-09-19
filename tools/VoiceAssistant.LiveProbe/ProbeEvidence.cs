using System.Reflection;
using System.Text.Json;

namespace VoiceAssistant.LiveProbe;

public sealed record ProbeTimings(
    double? AudioStartMs = null,
    double? InputSpeechEndMs = null,
    double? FinalSttMs = null,
    double? SpeechEndToFinalSttMs = null,
    double? FinalSttToFirstDeltaMs = null,
    double? FinalSttToCompletedMs = null,
    double MaxFrameLatenessMs = 0,
    double? SourceSpeechEndOffsetMs = null);

public sealed record ProbeEvidence(
    string Status, string Reason, string Provider, DateTimeOffset StartedAt,
    double ElapsedMs = 0, int PartialEvents = 0, int FinalEvents = 0, int DeltaEvents = 0,
    ProbeTimings? Timings = null, ServiceFailure? Failure = null, RecognitionQuality? RecognitionQuality = null)
{
    public int SchemaVersion => 1;
    public string Scope => "speech_openai_only";
    public string SpeechEndBoundary => "annotated_sample_frame_delivery_20ms_resolution_not_microphone_acoustic_end";
    public string? BuildSourceRevision => typeof(ProbeEvidence).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').Skip(1).FirstOrDefault();
    public string Provenance => "assembly_build_revision_not_dirty_worktree_attestation";
    public int ExitCode => Status switch { "SUCCESS" => 0, "BLOCKED" => 2, "CANCELLED" => 3, _ => 1 };
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
