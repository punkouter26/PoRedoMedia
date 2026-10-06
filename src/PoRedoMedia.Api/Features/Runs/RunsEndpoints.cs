using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Runs;

public static class RunsEndpoints
{
    private const int MaxOptions = 20;
    private const int MaxOptionLength = 2000;

    public static IEndpointRouteBuilder MapRuns(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/runs").RequireAntiforgeryValidation();
        group.MapPost("/", StartAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapDelete("/{id}", CancelAsync);
        return app;
    }

    /// <summary>
    /// Validates, then reserves the source's lane, then spends a quota credit, in that order: a
    /// request that is refused for any reason has spent nothing.
    /// </summary>
    private static async Task<Results<Accepted<RunDto>, NotFound, ProblemHttpResult>> StartAsync(
        RunRequest request, ClaimsPrincipal user, IMediaRepository media, IRunRepository runs,
        RunDispatcher dispatcher, RunExecutor executor, IRenderQuota quota,
        Features.MemeCaption.MemeTemplateService templates, CancellationToken ct)
    {
        var owner = UserId.From(user);
        var source = await media.GetAsync(owner, MediaId.From(request.SourceId), ct);
        if (source is not { Status: MediaStatus.Ready })
            return TypedResults.NotFound();

        var functions = request.Functions ?? [];
        if (FunctionStack.Validate(source.Kind, functions) is { } invalid)
            return Problem(invalid, StatusCodes.Status400BadRequest);
        var unavailable = functions.Where(f => !executor.Available.Contains(f)).ToArray();
        if (unavailable.Length > 0)
            return Problem($"{FunctionStack.Label(unavailable[0])} is not available.", StatusCodes.Status400BadRequest);

        var options = request.Options ?? [];
        if (options.Count > MaxOptions || options.Any(o => o.Key.Length > 64 || o.Value is null || o.Value.Length > MaxOptionLength))
            return Problem("The options are too large.", StatusCodes.Status400BadRequest);

        // Anything a step would refuse on sight is refused here, while it still costs nothing.
        var missing = FunctionStack.MissingOption(functions, options)
            ?? (functions.Contains(MediaFunction.MemeCaption) && options.GetValueOrDefault(RunOptions.MemeMode) == RunOptions.MemeModeTemplate
                ? templates.Problem(options)
                : null);
        if (missing is not null)
            return Problem(missing, StatusCodes.Status400BadRequest);

        if (!dispatcher.TryReserve(source.Id))
            return Problem("This item already has a run in progress.", StatusCodes.Status409Conflict);

        var run = new Run
        {
            Owner = owner,
            Id = RunId.New(),
            SourceId = source.Id,
            Functions = FunctionStack.InRunOrder(functions),
            Options = options,
            Status = RunStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var spent = false;
        try
        {
            var (allowed, status) = await quota.TryConsumeAsync(owner, CancellationToken.None);
            if (!allowed)
            {
                dispatcher.Release(source.Id);
                return Problem(
                    $"You have used all {status.Limit} runs for today. More are available at {status.ResetsAt:HH:mm} UTC.",
                    StatusCodes.Status429TooManyRequests);
            }

            spent = true;
            await runs.SaveAsync(run, CancellationToken.None);
        }
        catch
        {
            dispatcher.Release(source.Id);
            if (spent)
                await quota.RefundAsync(owner, CancellationToken.None);
            throw;
        }

        dispatcher.Queue(run);
        return TypedResults.Accepted($"/api/runs/{run.Id}", run.ToDto());
    }

    private static async Task<Ok<List<RunDto>>> ListAsync(ClaimsPrincipal user, IRunRepository runs, CancellationToken ct) =>
        TypedResults.Ok((await runs.ListAsync(UserId.From(user), ct)).Take(50).Select(r => r.ToDto()).ToList());

    private static async Task<Results<Ok<RunDto>, NotFound>> GetAsync(RunId id, ClaimsPrincipal user, IRunRepository runs, CancellationToken ct) =>
        await runs.GetAsync(UserId.From(user), id, ct) is { } run ? TypedResults.Ok(run.ToDto()) : TypedResults.NotFound();

    /// <summary>Stops the caller's queued or running run. Results it has already made are kept.</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> CancelAsync(
        RunId id, ClaimsPrincipal user, IRunRepository runs, RunDispatcher dispatcher, CancellationToken ct)
    {
        if (await runs.GetAsync(UserId.From(user), id, ct) is null)
            return TypedResults.NotFound();

        return dispatcher.Cancel(id)
            ? TypedResults.NoContent()
            : Problem("This run has already ended.", StatusCodes.Status409Conflict);
    }

    private static ProblemHttpResult Problem(string detail, int status) => TypedResults.Problem(detail: detail, statusCode: status);
}
