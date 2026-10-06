using Microsoft.AspNetCore.SignalR;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Runs;

/// <summary>Live progress for a run. Only a signed-in user can connect, and only the run's owner can join it.</summary>
public sealed class RunHub(IRunRepository runs) : Hub
{
    public const string Path = "/hubs/run";
    public const string ProgressEvent = "RunProgress";

    public static string Group(Guid runId) => $"run-{runId}";

    public async Task JoinRun(string runId)
    {
        if (!RunId.TryParse(runId, null, out var id)
            || await runs.GetAsync(UserId.From(Context.User!), id, Context.ConnectionAborted) is null)
        {
            throw new HubException("Run not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(id.Value), Context.ConnectionAborted);
    }
}

public sealed class RunHubNotifier(IHubContext<RunHub> hub) : IRunNotifier
{
    public Task ProgressAsync(RunProgressDto progress, CancellationToken ct = default) =>
        hub.Clients.Group(RunHub.Group(progress.RunId)).SendAsync(RunHub.ProgressEvent, progress, ct);
}
