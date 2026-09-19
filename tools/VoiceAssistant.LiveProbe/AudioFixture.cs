using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace VoiceAssistant.LiveProbe;

public sealed record FixtureMetadata(int SchemaVersion, bool Synthetic, bool ApprovedForLiveUse, string Language,
    string Sha256, int? SpeechEndSample, string? Text = null);

public sealed record AudioFixture(byte[] Pcm, int? SpeechEndSample, string? ReferenceText = null)
{
    public const int SampleRate = 16000;
    public const int BytesPerSecond = SampleRate * 2;
    public const int MaximumFileBytes = BytesPerSecond * 30 + 65536;
    public double DurationMs => Pcm.Length * 1000d / BytesPerSecond;

    public static AudioFixture Load(string wavPath, string metadataPath)
    {
        var bytes = ReadBounded(wavPath, MaximumFileBytes);
        var metadata = JsonSerializer.Deserialize<FixtureMetadata>(ReadBounded(metadataPath, 8192),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("invalid_metadata");
        return Parse(bytes, metadata);
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException("fixture_limit");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return data;
    }

    public static AudioFixture Parse(byte[] wav, FixtureMetadata metadata)
    {
        WordErrorRate.ValidateReference(metadata.Text);
        if (metadata.SchemaVersion != 1 || !metadata.Synthetic || !metadata.ApprovedForLiveUse ||
            metadata.Language != "en-US" || metadata.Sha256 is null || metadata.Sha256.Length != 64 ||
            !string.Equals(metadata.Sha256, Convert.ToHexString(SHA256.HashData(wav)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("fixture_not_approved");
        if (wav.Length < 44 || wav.Length > MaximumFileBytes ||
            !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)) != wav.Length - 8)
            throw new InvalidDataException("invalid_wave");
        var formatSeen = false;
        byte[]? pcm = null;
        var offset = 12;
        while (offset < wav.Length)
        {
            if (wav.Length - offset < 8) throw new InvalidDataException("invalid_chunk");
            var id = wav.AsSpan(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4));
            offset += 8;
            if (size > wav.Length - offset) throw new InvalidDataException("invalid_chunk");
            var chunk = wav.AsSpan(offset, (int)size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (formatSeen || chunk.Length is not (16 or 18) ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != SampleRate ||
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]) != BytesPerSecond ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]) != 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16 ||
                    (chunk.Length == 18 && BinaryPrimitives.ReadUInt16LittleEndian(chunk[16..]) != 0))
                    throw new InvalidDataException("unsupported_audio");
                formatSeen = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (pcm is not null || size == 0 || size % 2 != 0 || size > BytesPerSecond * 30)
                    throw new InvalidDataException("invalid_audio_data");
                pcm = chunk.ToArray();
            }
            offset += (int)size + (int)(size % 2);
            if (offset > wav.Length) throw new InvalidDataException("invalid_chunk_padding");
        }
        if (!formatSeen || pcm is null || pcm.Length < BytesPerSecond ||
            pcm.AsSpan(pcm.Length - BytesPerSecond).ContainsAnyExcept((byte)0) ||
            !pcm.AsSpan().ContainsAnyExcept((byte)0))
            throw new InvalidDataException("requires_speech_and_one_second_zero_tail");
        if (metadata.SpeechEndSample is { } end && (end <= 0 || end > pcm.Length / 2))
            throw new InvalidDataException("invalid_speech_end_sample");
        return new(pcm, metadata.SpeechEndSample, metadata.Text);
    }
}
