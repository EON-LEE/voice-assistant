using System.Text.Json;
using Azure.Core;
using ReplyQualityEval;
using VoiceAssistant.Api;

// Usage: ReplyQualityEval <out.json> <zip>|<meeting>[|<startSeconds>] ...
// Replays human-transcribed meetings with real timing through the production reply trigger and prompt,
// treats one participant as "ME", and scores timing (vs. when ME actually spoke) and suggestion quality.
var output = args[0];
var models = new Models(new EnvToken(), Environment.GetEnvironmentVariable("EVAL_OPENAI_ENDPOINT") ?? "https://voice-ai-2cmx22yjxadpg.openai.azure.com/",
    Environment.GetEnvironmentVariable("EVAL_CHAT_DEPLOYMENT") ?? "meeting-chat");
const double Window = 300;
var Settle = double.Parse(Environment.GetEnvironmentVariable("EVAL_SETTLE") ?? "2.5", System.Globalization.CultureInfo.InvariantCulture);
var gate = new SemaphoreSlim(3);
var results = new List<object>();
var rows = new List<string[]>();
foreach (var spec in args.Skip(1))
{
    var parts = spec.Split('|');
    var all = Corpus.Load(parts[0], parts[1]);
    var (start, target) = parts.Length > 2 ? (double.Parse(parts[2]), PickTarget(all, double.Parse(parts[2]))) : PickWindow(all);
    var clip = all.Where(u => u.End > start && u.End <= start + Window).ToList();
    var turns = MyTurns(clip, target);
    Console.WriteLine($"== {parts[1]} {start:F0}-{start + Window:F0}s target={target} utterances={clip.Count} myTurns={turns.Count}");
    var timing = new[] { 1.5, 2.5, 3.5 }.Append(Settle).Distinct().ToDictionary(s => s, s => Simulate(all, clip, s, target));
    var mode = Environment.GetEnvironmentVariable("EVAL_MODE") ?? "baseline";
    var candidates = mode == "own-voice" ? Simulate(all, clip, Settle, target, skipOwn: true)
        : timing[Settle];
    if (Environment.GetEnvironmentVariable("EVAL_TIMING_ONLY") == "1") candidates = [];
    await Task.WhenAll(candidates.Select(async candidate =>
    {
        await gate.WaitAsync();
        try
        {
            var history = all.Take(candidate.LastIndex + 1).ToList();
            await Task.WhenAll(models.Suggest(candidate, history), models.ClassifyTurn(candidate, history));
            var actual = clip.FirstOrDefault(u => u.Speaker == target && !Corpus.IsBackchannel(u.Text) &&
                u.Start > candidate.At - 1 && u.Start <= candidate.At + 10);
            await models.JudgeAsync(candidate, history, target, actual);
        }
        finally { gate.Release(); }
    }));
    var latency = candidates.Count == 0 ? 1.3 : candidates.Select(c => c.LatencySeconds).Order().ElementAt(candidates.Count / 2);
    foreach (var (name, filter) in new (string, Func<Candidate, bool>)[]
        { ("every unit", _ => true), ("questions only", c => c.Question), ("AI turn check", c => c.RespondNow),
          ("question or AI", c => c.Question || c.RespondNow) })
        rows.Add(Score(parts[1], name, candidates.Where(filter).ToList(), turns, latency));
    foreach (var s in new[] { 0.0, 0.5, 1.0, 1.5, 3.5 })
        rows.Add(Score(parts[1], $"every unit, settle {s}s (timing only)", s == 1.5 || s == 3.5 ? timing[s] : Simulate(all, clip, s, target), turns, latency, quality: false));
    foreach (var s in new[] { 0.0, 0.5, 1.0, 1.5 })
        rows.Add(Score(parts[1], $"own voice skipped, settle {s}s (timing only)", Simulate(all, clip, s, target, skipOwn: true), turns, latency, quality: false));
    results.Add(new
    {
        meeting = parts[1], start, target, myTurns = turns.Select(t => new { t.Start, t.Text }),
        medianLatency = latency,
        candidates = candidates.Select(c => new { c.At, c.Question, c.OwnSpeech, c.RespondNow, c.Primary, c.Alternative, c.LatencySeconds, c.Judge,
            heard = c.Pending })
    });
}
File.WriteAllText(output, JsonSerializer.Serialize(new { rows, results }, new JsonSerializerOptions { WriteIndented = true }));
var header = new[] { "meeting", "policy", "suggestions/min", "turn recall", "timely", "own speech", "relevance", "usefulness", "alignment" };
Console.WriteLine(string.Join(" | ", header));
foreach (var row in rows) Console.WriteLine(string.Join(" | ", row));
Console.WriteLine("-- overall (mean of meetings) --");
foreach (var policy in rows.Select(r => r[1]).Distinct())
{
    var group = rows.Where(r => r[1] == policy).ToList();
    string Mean(int i) => group.Select(r => r[i]).Where(v => v != "-").Select(v => double.Parse(v.TrimEnd('%'))).DefaultIfEmpty(double.NaN).Average() is var m && double.IsNaN(m) ? "-" : (group[0][i].EndsWith('%') ? $"{m:F0}%" : $"{m:F2}");
    Console.WriteLine(string.Join(" | ", new[] { "ALL", policy }.Concat(Enumerable.Range(2, 7).Select(Mean))));
}

static string[] Score(string meeting, string policy, List<Candidate> triggers, List<Utterance> turns, double latency, bool quality = true)
{
    var minutes = Window / 60;
    // A turn is covered when a suggestion is on screen by the time ME starts (1.5 s grace) and is not older than 8 s.
    var covered = turns.Count(t => triggers.Any(c => c.At >= t.Start - 8 && c.At + latency <= t.Start + 1.5));
    var timely = triggers.Count(c => turns.Any(t => t.Start >= c.At + latency - 1.5 && t.Start <= c.At + 8));
    var judged = triggers.Where(c => c.Judge is not null).ToList();
    var aligned = judged.Where(c => c.Judge!.Alignment is not null && turns.Any(t => t.Start >= c.At - 1 && t.Start <= c.At + 10)).ToList();
    string Pct(int n, int d) => d == 0 ? "-" : $"{100.0 * n / d:F0}%";
    string Avg(IEnumerable<int> v) => v.Any() ? $"{v.Average():F2}" : "-";
    return [meeting, policy, $"{triggers.Count / minutes:F1}", Pct(covered, turns.Count), Pct(timely, triggers.Count),
        Pct(triggers.Count(c => c.OwnSpeech), triggers.Count),
        quality ? Avg(judged.Select(c => c.Judge!.Relevance)) : "-", quality ? Avg(judged.Select(c => c.Judge!.Usefulness)) : "-",
        quality ? Avg(aligned.Select(c => c.Judge!.Alignment!.Value)) : "-"];
}

// Mirrors MeetingSession: wait for `settle` seconds of silence after a final, then ReplyTrigger decides.
static List<Candidate> Simulate(List<Utterance> all, List<Utterance> clip, double settle, string target, bool skipOwn = false)
{
    var candidates = new List<Candidate>();
    var pending = new List<Utterance>();
    double pendingSince = 0;
    foreach (var u in clip)
    {
        var index = all.IndexOf(u);
        // With voice separation, ME's own speech is context only: it answers what was pending and never triggers.
        if (skipOwn && u.Speaker == target) { pending.Clear(); continue; }
        if (pending.Count == 0) pendingSince = u.FinalAt;
        pending.Add(u);
        var nextSpeech = index + 1 < all.Count ? all[index + 1].Start + 0.5 : double.MaxValue;
        var longWait = u.FinalAt - pendingSince > 12;
        double? fires = nextSpeech > u.FinalAt + settle ? u.FinalAt + settle : longWait && nextSpeech > u.FinalAt + 0.3 ? u.FinalAt + 0.3 : null;
        if (fires is null) continue;
        var texts = pending.Select(p => p.Text).ToArray();
        var decision = ReplyTrigger.Evaluate(texts);
        if (decision == ReplyDecision.Wait) continue;
        if (decision == ReplyDecision.Reply)
            candidates.Add(new(fires.Value, index, texts, ReplyTrigger.ContainsQuestion(texts), pending[^1].Speaker == target));
        pending.Clear();
    }
    return candidates;
}

// ME takes the floor: a non-backchannel ME utterance right after (< 3 s) someone else's speech.
static List<Utterance> MyTurns(List<Utterance> clip, string target)
{
    var turns = new List<Utterance>();
    foreach (var u in clip.Where(u => u.Speaker == target && !Corpus.IsBackchannel(u.Text)).OrderBy(u => u.Start))
    {
        var previous = clip.Where(p => p.End <= u.Start + 0.2 && p != u).OrderBy(p => p.End).LastOrDefault();
        if (previous is not null && previous.Speaker != target && u.Start - previous.End < 3) turns.Add(u);
    }
    return turns;
}

static string PickTarget(List<Utterance> all, double start)
{
    var clip = all.Where(u => u.End > start && u.End <= start + Window).ToList();
    var dominant = clip.GroupBy(u => u.Speaker).OrderByDescending(g => g.Sum(u => u.Words)).First().Key;
    return clip.Select(u => u.Speaker).Distinct().Where(s => s != dominant)
        .OrderByDescending(s => MyTurns(clip, s).Count).FirstOrDefault() ?? dominant;
}

// The 5-minute window (after the first 2 minutes) where a non-dominant participant takes the floor most often.
static (double, string) PickWindow(List<Utterance> all)
{
    var best = (Start: 120.0, Speaker: "A", Turns: -1);
    for (var start = 120.0; start + Window <= all.Max(u => u.End); start += 30)
    {
        var speaker = PickTarget(all, start);
        var count = MyTurns(all.Where(u => u.End > start && u.End <= start + Window).ToList(), speaker).Count;
        if (count > best.Turns) best = (start, speaker, count);
    }
    return (best.Start, best.Speaker);
}

sealed class EnvToken : TokenCredential
{
    private static AccessToken Token => new(Environment.GetEnvironmentVariable("OPENAI_TOKEN")
        ?? throw new InvalidOperationException("Set OPENAI_TOKEN to an Azure OpenAI access token."), DateTimeOffset.UtcNow.AddMinutes(30));
    public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellation) => Token;
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellation) => new(Token);
}
