using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Api.Knowledge;

/// <summary>
/// Turns material titles and text into Speech phrase hints: titles, acronyms, product codes such as "ORN-4", CamelCase names,
/// capitalized multi-word names and single-word proper names ("Kestrova"). Ranked (titles first, then by frequency) and capped.
/// Material text only, never transcripts.
/// </summary>
internal static partial class MaterialPhrases
{
    internal const int MaxPhrases = 100;
    internal const int MaxLength = 50;
    private const int MaxTitles = 30;

    [Flags]
    private enum Kind { None = 0, Title = 1, Code = 2, Acronym = 4, Camel = 8, Name = 16, Word = 32 }

    private sealed class Candidate(string text, int order)
    {
        public string Text { get; } = text;
        public int Order { get; } = order;
        public int Count { get; set; }
        public Kind Kinds { get; set; }
        // A single word counts half as much as a code or a multi-word name, so frequent ones join without crowding those out.
        public int Score => Count * (Kinds.HasFlag(Kind.Code) ? 3 : Kinds == Kind.Word ? 1 : 2);
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
        // Single capitalized words: how often each occurs, and how often on its own in mid-sentence.
        var single = new Dictionary<string, (int Total, int Standalone)>(StringComparer.Ordinal);
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
                foreach (Match match in SingleWord().Matches(text))
                {
                    var word = match.Value;
                    if (lowercase.Contains(word.ToLowerInvariant()) || LeadingWords.Contains(word) || CommonWords.Contains(word)) continue;
                    single.TryGetValue(word, out var seen);
                    single[word] = (seen.Total + 1, seen.Standalone + (StandsAlone(text, match.Index) ? 1 : 0));
                }
            });
        }
        // A word never written in lower case, seen at least twice and at least once on its own in mid-sentence, is a name said on
        // its own ("Kestrova", "the Orrin gateway"). A word that only starts sentences or bullets ("Webhooks deliver...") is
        // ordinary, and one that only follows another capitalized word ("Traders" in "Northwind Traders") is left to that name.
        foreach (var (word, (total, standalone)) in single)
        {
            if (total < 2 || standalone == 0) continue;
            if (!candidates.TryGetValue(word, out var candidate)) candidates[word] = candidate = new(word, candidates.Count);
            candidate.Count += total;
            candidate.Kinds |= Kind.Word;
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

    /// <summary>
    /// Whether the word at <paramref name="index"/> stands on its own in mid-sentence: after a lower-case word, a function word
    /// ("the"), a number or a comma, colon or bracket. Not at the start of the text, a line, a sentence, a heading or a bullet, and
    /// not right after another capitalized word.
    /// </summary>
    private static bool StandsAlone(string text, int index)
    {
        var position = index - 1;
        while (position >= 0 && text[position] is ' ' or '\t') position--;
        if (position < 0 || text[position] is '\n' or '\r' or '.' or '!' or '?' or '#' or '*' or '-' or '>' or '\u2022') return false;
        if (!char.IsLetter(text[position])) return true;
        var end = position + 1;
        while (position >= 0 && char.IsLetter(text[position])) position--;
        var previous = text[(position + 1)..end];
        return !char.IsUpper(previous[0]) || LeadingWords.Contains(previous) || CommonWords.Contains(previous);
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
    [GeneratedRegex(@"\b[A-Z][a-z]{3,}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex SingleWord();
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
/// <see cref="CacheLifetime"/>). Search shows such a change only after a short delay, so a build applies the materials
/// service's own record of it (documents written but not yet listed, documents deleted but maybe still listed), and a build
/// that Search may not have caught up with is cached only for <see cref="ShortLifetime"/>. Building never throws; only counts
/// are logged.
/// </summary>
public sealed class MaterialPhraseHints(IKnowledgeStore store, TimeProvider clock, ILogger<MaterialPhraseHints> logger)
{
    internal const int MaxChunks = 1000;
    internal static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    /// <summary>How long after an upload or delete Search may still show the old materials.</summary>
    internal static readonly TimeSpan IndexLag = TimeSpan.FromSeconds(10);
    /// <summary>
    /// The lifetime of a build made within <see cref="IndexLag"/> of a change, or while Search did not show it yet, so it is
    /// rebuilt once Search has caught up.
    /// </summary>
    internal static readonly TimeSpan ShortLifetime = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Change> changes = new(StringComparer.Ordinal);

    /// <summary>
    /// The materials service's record of this user's latest change: documents written that Search may not list yet, and
    /// deleted documents it may still list. Like the service's own record, it keeps only what Search did not show yet.
    /// </summary>
    private sealed record Change(IReadOnlyList<KnowledgeDocument> Recent, IReadOnlySet<string> Deleted, long At);

    private sealed class Entry(Func<Entry, Task<IReadOnlyList<string>>> build, long created)
    {
        private readonly object gate = new();
        private Task<IReadOnlyList<string>>? phrases;
        private long lifetime = CacheLifetime.Ticks;
        public long Created { get; } = created;

        /// <summary>Set by the build: <see cref="ShortLifetime"/> while Search may not show the user's latest change yet.</summary>
        public TimeSpan Lifetime
        {
            get => TimeSpan.FromTicks(Volatile.Read(ref lifetime));
            set => Volatile.Write(ref lifetime, value.Ticks);
        }

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
        var entry = cache.AddOrUpdate(key, _ => NewEntry(key, now),
            (_, existing) => clock.GetElapsedTime(existing.Created) > existing.Lifetime ? NewEntry(key, now) : existing);
        return entry.Phrases;
    }

    /// <summary>Starts building in the background, so a session that starts soon finds the phrases ready.</summary>
    public void Warm(string owner) => _ = GetAsync(owner);

    /// <summary>
    /// Drops the cached phrases after this user's materials changed, keeping the materials service's record of the change
    /// (<paramref name="recent"/> written but maybe not listed yet, <paramref name="deleted"/> maybe still listed) for builds.
    /// </summary>
    public void Invalidate(string owner, IReadOnlyCollection<KnowledgeDocument>? recent = null, IReadOnlyCollection<string>? deleted = null)
    {
        string key;
        try { key = KnowledgeIds.Owner(owner); }
        catch (KnowledgeException) { return; }
        // Recorded before the cache entry goes, so a build that starts right after the removal already sees it.
        changes[key] = new([.. recent ?? []], (deleted ?? []).ToHashSet(StringComparer.Ordinal), clock.GetTimestamp());
        cache.TryRemove(key, out _);
    }

    private Entry NewEntry(string owner, long now) => new(entry => BuildAsync(owner, entry), now);

    private async Task<IReadOnlyList<string>> BuildAsync(string owner, Entry entry)
    {
        var started = clock.GetTimestamp();
        // Taken before reading Search, so what Search shows below is at least as new as this record.
        changes.TryGetValue(owner, out var change);
        try
        {
            using var timeout = new CancellationTokenSource(BuildTimeout);
            var documents = await store.ListAsync(owner, timeout.Token);
            var texts = await store.ReadTextAsync(owner, MaxChunks, timeout.Token);
            if (change is not null)
            {
                var listed = documents;
                // Search may still list a deleted document or miss a new one; the service's own record of the change wins.
                var recent = change.Recent.Where(document => !change.Deleted.Contains(document.Id)).ToArray();
                documents = listed.Where(document => !change.Deleted.Contains(document.Id) && recent.All(added => added.Id != document.Id))
                    .Concat(recent).OrderByDescending(document => document.UpdatedAt).ToArray();
                texts = texts.Where(text => KnowledgeIds.DocumentOf(text.Id) is not { } id || !change.Deleted.Contains(id)).ToArray();
                var pending = change with
                {
                    Recent = recent.Where(document => !listed.Any(shown => shown.Id == document.Id && shown.Chunks == document.Chunks)).ToArray(),
                    Deleted = change.Deleted.Where(id => listed.Any(shown => shown.Id == id)).ToHashSet(StringComparer.Ordinal),
                };
                // What Search shows now needs no record (as in the service), so the record cannot outlive the change; a newer one wins.
                if (pending.Recent.Count < change.Recent.Count || pending.Deleted.Count < change.Deleted.Count) changes.TryUpdate(owner, pending, change);
                if (pending.Recent.Count > 0 || pending.Deleted.Count > 0 || clock.GetElapsedTime(change.At, started) < IndexLag)
                    entry.Lifetime = ShortLifetime;
            }
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
