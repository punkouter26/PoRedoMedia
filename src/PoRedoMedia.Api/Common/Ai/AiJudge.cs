using Microsoft.Extensions.AI;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// Scores drafts against a rubric, so a writer can ask for several and keep the best. Short
/// outputs (a caption, a one-line joke) cost a few output tokens each, and picking the best of a
/// handful lifts them more than any single prompt change. The same scores drive the offline
/// evaluation (<c>eval-ai</c>), so what is measured there is what picks here.
/// </summary>
public sealed partial class AiJudge(IChatClient chat, ILogger<AiJudge> logger)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Judge ({Task}) scored {Count} draft(s): {Scores}; kept #{Best}")]
    private partial void LogPicked(string task, int count, string scores, int best);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Judge ({Task}) failed; keeping the first draft")]
    private partial void LogFailed(Exception ex, string task);

    private const string SystemPrompt =
        "You are a strict judge of short comedy writing. You get a task, a rubric and numbered drafts. "
        + "Score every draft from 1 (fails the rubric) to 10 (excellent) on the rubric alone, comparing "
        + "the drafts with each other so the scores spread out. A draft that breaks a safety rule in the "
        + "rubric scores 1 however funny it is. Return one score per draft, by its number.";

    /// <summary>One score from 1 to 10 per draft, in draft order. Throws when the judge gives no usable answer.</summary>
    public async Task<double[]> ScoreAsync(string task, string rubric, IReadOnlyList<string> drafts, CancellationToken ct = default)
    {
        var numbered = string.Join('\n', drafts.Select((d, i) => $"[{i}] {d}"));
        var response = await chat.GetResponseAsync<Verdict>(
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, $"TASK: {task}\nRUBRIC: {rubric}\nDRAFTS:\n{numbered}"),
        ], cancellationToken: ct);

        if (!response.TryGetResult(out var verdict) || verdict.Scores is not { Length: > 0 })
            throw new InvalidOperationException("The judge returned no scores.");

        var scores = new double[drafts.Count];
        foreach (var s in verdict.Scores.Where(s => s.Draft >= 0 && s.Draft < drafts.Count))
            scores[s.Draft] = Math.Clamp(s.Score, 1, 10);
        return scores;
    }

    /// <summary>
    /// The index of the best draft. A failed judge is not a failed output: it keeps draft 0, which
    /// is what the writer would have returned without one.
    /// </summary>
    public async Task<int> PickAsync(string task, string rubric, IReadOnlyList<string> drafts, CancellationToken ct = default)
    {
        if (drafts.Count < 2)
            return 0;

        try
        {
            var scores = await ScoreAsync(task, rubric, drafts, ct);
            var best = Array.IndexOf(scores, scores.Max());
            LogPicked(task, drafts.Count, string.Join(", ", scores), best);
            return best;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFailed(ex, task);
            return 0;
        }
    }

    private sealed record Verdict(DraftScore[] Scores);

    private sealed record DraftScore(int Draft, double Score);
}
