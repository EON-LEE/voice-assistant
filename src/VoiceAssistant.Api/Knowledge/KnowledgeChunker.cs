namespace VoiceAssistant.Api.Knowledge;

public static class KnowledgeChunker
{
    public static IReadOnlyList<KnowledgeChunk> Split(string id, string title, string text)
    {
        if (!KnowledgeIds.ValidDocument(id)) throw KnowledgeException.Invalid();
        if (text.Length > KnowledgeLimits.MaxCharactersPerDocument) throw KnowledgeException.TooLarge();
        if (string.IsNullOrWhiteSpace(text)) throw KnowledgeException.NoText();
        var source = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var chunks = new List<KnowledgeChunk>();
        var headings = new List<(int Offset, string Text)>();
        var position = 0;
        foreach (var line in source.Split('\n'))
        {
            var value = line.Trim();
            if (value.StartsWith('#') && value.Length <= 160)
                headings.Add((position, value));
            position += line.Length + 1;
        }
        var start = 0;
        while (start < source.Length)
        {
            var heading = headings.LastOrDefault(item => item.Offset <= start).Text;
            var prefix = title + (heading is null ? "" : " | " + heading) + "\n";
            var capacity = 1400 - prefix.Length;
            var end = Math.Min(source.Length, start + capacity);
            if (end < source.Length)
            {
                var boundary = source.LastIndexOf('\n', end - 1, Math.Max(1, end - start - Math.Min(700, capacity / 2)));
                if (boundary >= start + 400) end = boundary + 1;
                if (end > start && char.IsHighSurrogate(source[end - 1]) && char.IsLowSurrogate(source[end])) end--;
            }
            var body = source[start..end];
            if (!string.IsNullOrWhiteSpace(body))
                chunks.Add(new($"kb-{id}-{chunks.Count:D4}", prefix + body));
            if (end == source.Length) break;
            var next = Math.Max(start + 1, end - 150);
            if (char.IsLowSurrogate(source[next]) && next > 0 && char.IsHighSurrogate(source[next - 1])) next--;
            start = next;
        }
        return chunks;
    }
}
