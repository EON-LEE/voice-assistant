using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Api.Knowledge;

/// <summary>
/// Turns material titles and text into Speech phrase hints: titles, acronyms, product codes such as "ORN-4", CamelCase names
/// and capitalized multi-word names. Ranked (titles first, then by frequency) and capped. Material text only, never transcripts.
/// </summary>
internal static partial class MaterialPhrases
{
    internal const int MaxPhrases = 100;
    internal const int MaxLength = 50;
    private const int MaxTitles = 30;

    [Flags]
    private enum Kind { None = 0, Title = 1, Code = 2, Acronym = 4, Camel = 8, Name = 16 }

    private sealed class Candidate(string text, int order)
    {
        public string Text { get; } = text;
        public int Order { get; } = order;
        public int Count { get; set; }
        public Kind Kinds { get; set; }
        public int Score => Count * (Kinds.HasFlag(Kind.Code) ? 3 : 2);
    }

    internal static IReadOnlyList<string> Extract(IReadOnlyList<string> titles, IReadOnlyList<string> texts)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        void Add(string raw, Kind kind)
        {
            var phrase = Normalize(raw);
            if (phrase.Length is < 2 or > MaxLength || !phrase.Any(char.IsLetter)) return;
            if (!candidates.TryGetValue(phrase, out var candidate)) candidates[phrase] = candidate = new(phrase, candidates.Count);
            candidate.Count++;
            candidate.Kinds |= kind;
        }

        var sources = texts.Concat(titles).ToArray();
        // An all-capital word that also appears in lower case is a shouted heading word ("OVERVIEW"), not an acronym.
        var lowercase = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in sources)
            Scan(text, () => { foreach (Match word in LowercaseWord().Matches(text)) lowercase.Add(word.Value); });
        foreach (var title in titles) Add(TitleText(title), Kind.Title);
        foreach (var text in sources)
        {
            Scan(text, () =>
            {
                foreach (Match match in ProductCode().Matches(text)) Add(match.Value, Kind.Code);
                foreach (Match match in Acronym().Matches(text))
                    if (!CommonAcronyms.Contains(match.Value) && !lowercase.Contains(match.Value.ToLowerInvariant())) Add(match.Value, Kind.Acronym);
                foreach (Match match in CamelCase().Matches(text)) Add(match.Value, Kind.Camel);
                foreach (Match match in CapitalizedRun().Matches(text))
                    if (NameFrom(match.Value) is { } name) Add(name, Kind.Name);
            });
        }

        // Titles first (most recent documents first, as listed), then the most frequent terms. A capitalized name needs two
        // occurrences, so a sentence-initial capital rarely qualifies.
        var titled = candidates.Values.Where(candidate => candidate.Kinds.HasFlag(Kind.Title)).OrderBy(candidate => candidate.Order).Take(MaxTitles);
        var terms = candidates.Values
            .Where(candidate => !candidate.Kinds.HasFlag(Kind.Title) && (candidate.Kinds != Kind.Name || candidate.Count >= 2))
            .OrderByDescending(candidate => candidate.Score).ThenByDescending(candidate => candidate.Text.Length).ThenBy(candidate => candidate.Order);
        return titled.Concat(terms).Select(candidate => candidate.Text).Take(MaxPhrases).ToArray();
    }

    /// <summary>The client's phrases first, then material phrases it does not already have (case-insensitive), at most <see cref="MaxPhrases"/>.</summary>
    internal static IReadOnlyList<string> Merge(IReadOnlyList<string> client, IReadOnlyList<string> materials)
    {
        var seen = new HashSet<string>(client, StringComparer.OrdinalIgnoreCase);
        return client.Concat(materials.Where(seen.Add).Take(MaxPhrases)).ToArray();
    }

    private static void Scan(string text, Action scan)
    {
        // A pathological text is skipped rather than allowed to stall a session start.
        try { scan(); }
        catch (RegexMatchTimeoutException) { }
    }

    private static string TitleText(string title)
    {
        var text = FileExtension().Replace(title.Trim(), "");
        return text.Replace('_', ' ');
    }

    private static string Normalize(string phrase) =>
        Whitespace().Replace(phrase, " ").Trim().Trim('"', '\'', '(', ')', '[', ']', '.', ',', ':', ';', '!', '?', '-', '/');

    /// <summary>A capitalized run without its leading function words; null when fewer than two words or only common words remain.</summary>
    private static string? NameFrom(string run)
    {
        var words = run.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(LeadingWords.Contains).ToArray();
        if (words.Length < 2 || words.All(CommonWords.Contains)) return null;
        return string.Join(' ', words);
    }

    private static readonly HashSet<string> CommonAcronyms = new(StringComparer.Ordinal)
    {
        "AM", "PM", "OK", "THE", "AND", "OR", "FOR", "TO", "OF", "IN", "ON", "AT", "IS", "IT", "BE", "WE", "US", "NO", "YES", "NOT",
        "ALL", "NEW", "TBD", "TBC", "FYI", "ASAP", "ETC", "EG", "IE", "VS", "NA", "QA", "FAQ", "TODO", "NOTE", "PS", "CC", "RE", "FW", "FWD"
    };

    private static readonly HashSet<string> LeadingWords = new(StringComparer.Ordinal)
    {
        "The", "A", "An", "This", "That", "These", "Those", "Our", "Your", "Their", "His", "Her", "We", "I", "You", "They", "He", "She",
        "It", "Its", "My", "In", "On", "At", "For", "And", "But", "Or", "If", "When", "While", "With", "To", "Of", "By", "From", "As",
        "Is", "Are", "Was", "Were", "Be", "How", "What", "Why", "Who", "Where", "Which", "Please", "So", "Also", "Then", "There", "Here",
        "All", "Each", "Every", "Any", "Some", "No", "Not", "Yes", "Can", "Could", "Will", "Would", "Should", "Do", "Does", "Did"
    };

    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "Next", "Steps", "Step", "Key", "Points", "Action", "Items", "Thank", "Thanks", "You", "Questions", "Agenda", "Summary",
        "Overview", "Introduction", "Background", "Goal", "Goals", "Objectives", "Results", "Status", "Update", "Updates", "Plan",
        "Timeline", "Risk", "Risks", "Issues", "Notes", "Appendix", "Conclusion", "Discussion", "Recap", "Today", "Tomorrow", "Team",
        "Meeting", "Project", "Review", "Weekly", "Monthly", "Daily", "Report", "Executive", "Section", "Slide", "Page", "Table",
        "Figure", "Contents", "Part", "Phase", "Best", "Regards", "Hi", "Hello", "Dear", "Monday", "Tuesday", "Wednesday", "Thursday",
        "Friday", "Saturday", "Sunday", "January", "February", "March", "April", "May", "June", "July", "August", "September",
        "October", "November", "December"
    };

    [GeneratedRegex(@"\b(?=[A-Z0-9-]*\d)[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,10})?\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex ProductCode();
    [GeneratedRegex(@"\b[A-Z]{2,10}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Acronym();
    [GeneratedRegex(@"\b[A-Z][a-z]+(?:[A-Z][a-z0-9]+)+\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex CamelCase();
    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9]+(?: [A-Z][A-Za-z0-9]+){1,4}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex CapitalizedRun();
    [GeneratedRegex(@"\b[a-z]{2,}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex LowercaseWord();
    [GeneratedRegex(@"\.(?:pptx|docx|pdf|txt|md)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FileExtension();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <summary>
/// Speech phrase hints from the caller's own materials (experiment switch <c>Speech:MaterialPhraseHints</c>). Built from the
/// ACL-filtered material text, cached per user, and dropped when that user uploads or deletes materials (or after
/// <see cref="CacheLifetime"/>). Building never throws; only counts are logged, never the phrases.
/// </summary>
public sealed class MaterialPhraseHints(IKnowledgeStore store, TimeProvider clock, ILogger<MaterialPhraseHints> logger)
{
    internal const int MaxChunks = 1000;
    internal static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Entry> cache = new(StringComparer.Ordinal);

    private sealed class Entry(Func<Entry, Task<IReadOnlyList<string>>> build, long created)
    {
        private readonly object gate = new();
        private Task<IReadOnlyList<string>>? phrases;
        public long Created { get; } = created;

        /// <summary>Started on first use only, so an entry that lost a race for the cache slot never builds.</summary>
        public Task<IReadOnlyList<string>> Phrases
        {
            get
            {
                lock (gate) return phrases ??= Task.Run(() => build(this));
            }
        }
    }

    /// <summary>This user's material phrases, built on a cache miss. Never throws: a failure yields none and is not cached.</summary>
    public Task<IReadOnlyList<string>> GetAsync(string owner)
    {
        string key;
        try { key = KnowledgeIds.Owner(owner); }
        catch (KnowledgeException) { return Task.FromResult<IReadOnlyList<string>>([]); }
        var now = clock.GetTimestamp();
        var entry = cache.AddOrUpdate(key, _ => new Entry(BuildFor(key), now),
            (_, existing) => clock.GetElapsedTime(existing.Created) > CacheLifetime ? new Entry(BuildFor(key), now) : existing);
        return entry.Phrases;
    }

    /// <summary>Starts building in the background, so a session that starts soon finds the phrases ready.</summary>
    public void Warm(string owner) => _ = GetAsync(owner);

    /// <summary>Drops the cached phrases after this user's materials changed.</summary>
    public void Invalidate(string owner)
    {
        try { cache.TryRemove(KnowledgeIds.Owner(owner), out _); }
        catch (KnowledgeException) { }
    }

    private Func<Entry, Task<IReadOnlyList<string>>> BuildFor(string owner) => entry => BuildAsync(owner, entry);

    private async Task<IReadOnlyList<string>> BuildAsync(string owner, Entry entry)
    {
        var started = clock.GetTimestamp();
        try
        {
            using var timeout = new CancellationTokenSource(BuildTimeout);
            var documents = await store.ListAsync(owner, timeout.Token);
            var texts = await store.ReadTextAsync(owner, MaxChunks, timeout.Token);
            var phrases = MaterialPhrases.Extract(documents.Select(document => document.Title).ToArray(),
                texts.Select(text => text.Content).ToArray());
            logger.LogInformation("Material phrase hints built: {Count} phrases from {Documents} documents and {Chunks} chunks in {ElapsedMs} ms.",
                phrases.Count, documents.Count, texts.Count, (long)clock.GetElapsedTime(started).TotalMilliseconds);
            return phrases;
        }
        catch (Exception exception)
        {
            // Not cached, so the next session tries again.
            cache.TryRemove(new KeyValuePair<string, Entry>(owner, entry));
            logger.LogWarning("Material phrase hints are unavailable ({ExceptionType}); speech uses the client phrases only.",
                exception.GetType().Name);
            return [];
        }
    }
}
