using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using VoiceAssistant.Audio;

namespace VoiceAssistant.WebOverlay.HostAudio;

internal enum HostAudioKind { Teams, System }

internal enum HostAudioState { Capturing, TeamsNotRunning, UnsupportedOs, Error, Stopped }

internal abstract record HostAudioCommand(string? Id);
internal sealed record HostAudioStart(HostAudioKind Kind, string? Id) : HostAudioCommand(Id);
internal sealed record HostAudioStop(string? Id) : HostAudioCommand(Id);
internal sealed record HostAudioAck(long Seq, string? Id) : HostAudioCommand(Id);

/// <summary>
/// JSON web messages between the hosted live page and LiveCoach.exe.
/// Page to host: <c>host-audio.start {source: "teams"|"system", id}</c>, <c>host-audio.stop {id}</c> and
/// <c>host-audio.ack {seq, id}</c> (the page acknowledges frames it handled; see <see cref="HostAudioOutbox"/>).
/// Host to page: <c>host-audio.status {id, source, state, detail}</c> and <c>host-audio.frame {seq, pcm}</c>, where
/// <c>pcm</c> is base64 of 1 to 5 consecutive 20 ms frames (640 bytes each, 16 kHz mono PCM16LE) and <c>seq</c> numbers
/// the first of them. A capture starts at seq 0; a gap in seq means the host dropped frames.
/// </summary>
internal static class HostAudioProtocol
{
    public const int MaxCommandChars = 512;
    public const int MaxIdChars = 64;
    public const int MaxFramesPerMessage = 5;

    public static bool IsJsonObject(string message) => message.Length > 0 && message[0] == '{';

    /// <summary>Returns the page command, or null for anything that is not a well-formed host-audio command.</summary>
    public static HostAudioCommand? ParseCommand(string json)
    {
        if (json.Length > MaxCommandChars || !IsJsonObject(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return null;
            string? id = null;
            if (root.TryGetProperty("id", out var idValue))
            {
                if (idValue.ValueKind != JsonValueKind.String || !IsValidId(idValue.GetString()!)) return null;
                id = idValue.GetString();
            }
            switch (type.GetString())
            {
                case "host-audio.start":
                    if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String) return null;
                    return source.GetString() switch
                    {
                        "teams" => new HostAudioStart(HostAudioKind.Teams, id),
                        "system" => new HostAudioStart(HostAudioKind.System, id),
                        _ => null,
                    };
                case "host-audio.stop":
                    return new HostAudioStop(id);
                case "host-audio.ack":
                    return root.TryGetProperty("seq", out var seq) && seq.ValueKind == JsonValueKind.Number &&
                        seq.TryGetInt64(out var value) && value >= 0 ? new HostAudioAck(value, id) : null;
                default:
                    return null;
            }
        }
        catch (JsonException) { return null; }
    }

    private static bool IsValidId(string id) =>
        id.Length is > 0 and <= MaxIdChars && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string Wire(HostAudioState state) => state switch
    {
        HostAudioState.Capturing => "capturing",
        HostAudioState.TeamsNotRunning => "teams-not-running",
        HostAudioState.UnsupportedOs => "unsupported-os",
        HostAudioState.Error => "error",
        HostAudioState.Stopped => "stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static string Wire(HostAudioKind kind) => kind == HostAudioKind.Teams ? "teams" : "system";

    public static string Status(HostAudioState state, string detail, HostAudioKind kind, string? id)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "host-audio.status");
            if (id is not null) writer.WriteString("id", id);
            writer.WriteString("source", Wire(kind));
            writer.WriteString("state", Wire(state));
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Frames(long firstSeq, ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty || pcm.Length % Pcm16Framer.FrameBytes != 0 || pcm.Length > MaxFramesPerMessage * Pcm16Framer.FrameBytes)
            throw new ArgumentException("A frame message carries 1 to 5 whole 640-byte frames.", nameof(pcm));
        return "{\"type\":\"host-audio.frame\",\"seq\":" + firstSeq.ToString(CultureInfo.InvariantCulture) +
            ",\"pcm\":\"" + Convert.ToBase64String(pcm) + "\"}";
    }
}
