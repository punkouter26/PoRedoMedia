using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Memeify;

/// <summary>What the engine hands the director: the moments that get a cue and the sounds it may pick from.</summary>
/// <param name="Log">Director's Log lines describing the plan, in the order they happened.</param>
internal sealed record PlacementPlan(
    IReadOnlyList<TimingDecision> Decisions,
    SceneLabel[] ApprovedLabels,
    IReadOnlyList<SoundAsset> DirectorMenu,
    IReadOnlyList<string> Log);

/// <summary>
/// Turns scene labels (vision plus speech) and their ranked sound matches into timed placements
/// and a sound menu for the director.
/// </summary>
/// <remarks>
/// Pure: no storage, no AI, no hub. It was 120 lines in the middle of
/// <see cref="RunEngineCommand"/>'s run method, interleaved with the log calls that described it;
/// the log lines now come back with the plan and the engine sends them.
/// </remarks>
internal static class PlacementPlanner
{
    /// <summary>Most speech lines turned into placement candidates; the token bucket thins them further.</summary>
    private const int MaxSpeechCues = 8;

    /// <summary>A reaction lands just after the line ends, not on top of its last word.</summary>
    private const double SpeechReactionDelaySeconds = 0.15;

    /// <summary>Score multiplier for sounds the user has starred.</summary>
    private const float FavoriteBoost = 1.5f;

    private const int DirectorMenuCap = 48;
    private const int FavoritesInMenuCap = 12;

    /// <summary>Prefix of the scene labels made from speech; their timing is deliberate, never snapped.</summary>
    internal const string SpeechLabelPrefix = "after the line";

    /// <summary>
    /// Vision labels plus one label per speech line, timed just after the line ends — that is
    /// where a reaction sound lands. Both kinds go through the token bucket together, so they are
    /// spaced against each other. Output time, in time order.
    /// </summary>
    public static List<SceneLabel> SceneLabels(IReadOnlyList<SceneLabel> visionLabels, IReadOnlyList<TranscriptSegmentDto> transcript, double durationSeconds)
    {
        var labels = visionLabels.ToList();
        foreach (var seg in transcript.Take(MaxSpeechCues))
        {
            var ts = Math.Min(seg.EndSeconds + SpeechReactionDelaySeconds, Math.Max(0, durationSeconds - 0.4));
            labels.Add(new SceneLabel(ts, $"{SpeechLabelPrefix} \"{Truncate(seg.Text, 80)}\"") { Energy = 0.6 });
        }
        labels.Sort((a, b) => a.TimestampSeconds.CompareTo(b.TimestampSeconds));
        return labels;
    }

    /// <summary>What matching looks up for a label: its words plus the library tags vision chose.</summary>
    public static string MatchQuery(SceneLabel label)
        => label.Tags.Count == 0 ? label.Label : $"{label.Label} {string.Join(' ', label.Tags)}";

    /// <param name="ranked">Match candidates per label, same order as <paramref name="sceneLabels"/>.</param>
    public static PlacementPlan Plan(
        IReadOnlyList<SceneLabel> sceneLabels,
        IReadOnlyList<IReadOnlyList<SoundCandidate>> ranked,
        IReadOnlyList<SoundAsset> library,
        IReadOnlySet<SoundId> favorites,
        double durationSeconds)
    {
        var log = new List<string>();
        var requests = new List<PlacementRequest>();
        var menu = new Dictionary<SoundId, SoundAsset>();
        var prioritySounds = library.Where(s => s.Priority).ToList();
        var provisionalIdx = 0;

        // Labels whose words miss the tag vocabulary still get a provisional priority sound —
        // the director re-picks from the full menu.
        for (var i = 0; i < sceneLabels.Count; i++)
        {
            var label = sceneLabels[i];
            var candidates = RankWithFavorites(ranked[i], favorites);
            foreach (var c in candidates)
                menu[c.Sound.SoundId] = c.Sound;

            log.Add($"SCANNING... t={label.TimestampSeconds:F1}s | ACTION: [{label.Label.ToUpperInvariant()}] | ENERGY {label.Energy:F1}");
            var atMs = (long)Math.Round(label.TimestampSeconds * 1000);
            if (candidates.Count > 0)
            {
                var best = candidates[0];
                log.Add($"SELECTED: {best.Sound.DisplayName} (accuracy={best.Score:F2}, {candidates.Count} candidate(s))");
                requests.Add(new(atMs, best.Sound, PlacementScore(best.Score, label.Energy)));
            }
            else if (library.Count > 0)
            {
                var pool = prioritySounds.Count > 0 ? prioritySounds : library;
                var provisional = pool[provisionalIdx++ % pool.Count];
                log.Add($"NO TAG MATCH — PROVISIONAL: {provisional.DisplayName}. AI DIRECTOR WILL CHOOSE FINAL SOUND.");
                requests.Add(new(atMs, provisional, PlacementScore(0.4f, label.Energy)));
            }
        }

        if (requests.Count == 0 && library.Count > 0)
        {
            AddTimeBasedFallback(requests, library, durationSeconds);
            log.Add($"TIME-BASED FALLBACK: {requests.Count} placement(s) every 2s (video={durationSeconds:F1}s, cap=10s).");
        }

        var decisions = Thin(
            new TokenBucketTimingService().Apply(requests, durationSeconds, library.Count > 0 ? library[0] : null),
            DirectorScripts.MaxCues);
        log.Add($"TOKEN BUCKET: {decisions.Count} placement(s) approved.");
        foreach (var d in decisions)
        {
            log.Add(d.PlacementType != PlacementType.Triggered
                ? d.AuditMessage
                : $"PLACED: {d.SelectedSound.DisplayName} @ {d.ApprovedTimestampMs}ms [{d.PlacementType}]");
        }

        // Menu: approved picks + per-label candidates + favourites + the curated priority set,
        // capped to keep the prompt small. The render resolves any library sound, so every menu
        // pick is renderable.
        foreach (var d in decisions)
            menu[d.SelectedSound.SoundId] = d.SelectedSound;
        foreach (var s in library.Where(s => favorites.Contains(s.SoundId)).Take(FavoritesInMenuCap))
            menu.TryAdd(s.SoundId, s);
        foreach (var s in prioritySounds)
        {
            if (menu.Count >= DirectorMenuCap) break;
            menu.TryAdd(s.SoundId, s);
        }

        return new PlacementPlan(decisions, ApprovedLabels(decisions, sceneLabels, durationSeconds), [.. menu.Values.Take(DirectorMenuCap)], log);
    }

    /// <summary>
    /// At most <paramref name="max"/> of the decisions, spread evenly so a long clip keeps cues
    /// to its end instead of losing everything after the cap.
    /// </summary>
    internal static IReadOnlyList<TimingDecision> Thin(IReadOnlyList<TimingDecision> decisions, int max)
        => decisions.Count <= max
            ? decisions
            : [.. Enumerable.Range(0, max).Select(i => decisions[(int)((long)i * decisions.Count / max)])];

    /// <summary>
    /// A match's claim on its 2-second window, weighted by how much the moment hits: at equal fit
    /// a slam beats a shrug. Energy 0.5 (unknown) leaves the score unchanged.
    /// </summary>
    internal static float PlacementScore(float matchScore, double energy)
        => matchScore * (float)(0.5 + Math.Clamp(energy, 0, 1));

    /// <summary>Starred sounds outrank equally good matches, keeping the top three.</summary>
    internal static IReadOnlyList<SoundCandidate> RankWithFavorites(IReadOnlyList<SoundCandidate> candidates, IReadOnlySet<SoundId> favorites)
        => candidates
            .Select(c => favorites.Contains(c.Sound.SoundId) ? c with { Score = c.Score * FavoriteBoost } : c)
            .OrderByDescending(c => c.Score)
            .Take(3)
            .ToList();

    /// <summary>
    /// No labels matched anything (no keyframes, or nothing in the library fits): a sound every
    /// 2 seconds over the first 10, preferring unused curated sounds, and always at least one.
    /// </summary>
    private static void AddTimeBasedFallback(List<PlacementRequest> requests, IReadOnlyList<SoundAsset> library, double durationSeconds)
    {
        var windowMs = Math.Min((long)(durationSeconds * 1000), 10_000L);
        var times = new List<long>();
        for (var t = 0L; t < windowMs; t += 2_000L)
            times.Add(t);
        if (times.Count == 0) times.Add(0);

        foreach (var t in times)
        {
            var used = requests.Select(p => p.SelectedSound.SoundId).ToHashSet();
            var available = library.Where(s => !used.Contains(s.SoundId)).ToList();
            if (available.Count == 0) available = [.. library];

            var priorityPool = available.Where(s => s.Priority).ToList();
            var pool = priorityPool.Count > 0 ? priorityPool : available;
            requests.Add(new(t, pool[Random.Shared.Next(pool.Count)], 0.5f));
        }
    }

    /// <summary>
    /// Each decision's nearest scene label, moved to the decision's time. A decision with no
    /// usable label gets a positional description so the director can still reason about timing.
    /// </summary>
    private static SceneLabel[] ApprovedLabels(IReadOnlyList<TimingDecision> decisions, IReadOnlyList<SceneLabel> sceneLabels, double durationSeconds)
    {
        var duration = durationSeconds > 0 ? durationSeconds : 1.0;
        return [.. decisions.Select(d =>
        {
            var at = d.ApprovedTimestampMs / 1000.0;
            var original = sceneLabels.OrderBy(v => Math.Abs(v.TimestampSeconds - at)).FirstOrDefault();
            if (original is null || string.IsNullOrWhiteSpace(original.Label) || original.Label == "unknown")
            {
                var relPos = at / duration;
                return new SceneLabel(at, relPos < 0.25 ? "opening scene" : relPos < 0.6 ? "mid-video action" : "final moments");
            }
            return original with { TimestampSeconds = at };
        })];
    }

    internal static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}
