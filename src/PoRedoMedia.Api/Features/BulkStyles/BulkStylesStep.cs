using System.Security.Claims;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.BulkStyles;

/// <summary>
/// Redraws the picture once per style prompt, a few at a time, and reports each variation as it
/// finishes. One failed variation does not fail the others.
/// </summary>
public sealed class BulkStylesStep(
    IImageGenerationService images, BulkPromptRepository saved, BlobStorageService blobs, MediaOutputs outputs,
    ILogger<BulkStylesStep> logger) : IRunStep
{
    /// <summary>How many variations are drawn at once. More than this trips the provider's rate limit.</summary>
    private const int Concurrency = 4;

    public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { MediaFunction.BulkStyles };

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var prompts = Prompts(context.Run.Options, await saved.GetAsync(context.Run.Owner, ct));
        var image = ImageBytes.ForProcessing(await blobs.ReadAllBytesAsync(context.Current.SourcePath, ct));

        using var gate = new SemaphoreSlim(Concurrency);
        var drawing = prompts.Select(async prompt =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // The picture itself is sent, so the token only has to point at it.
                return (prompt, Image: await images.EditAsync(
                    prompt.Replace(BulkPrompts.SubjectToken, "the subject of the reference photo", StringComparison.Ordinal), image, ct: ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Bulk variation failed: {Title}", BulkPrompts.Title(prompt));
                return (prompt, Image: (GeneratedImage?)null);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        // Drawn in parallel, saved one at a time: the run's record of its outputs is not thread-safe.
        var made = 0;
        await foreach (var finished in Task.WhenEach(drawing))
        {
            var (prompt, drawn) = await finished;
            if (drawn is null)
            {
                context.AddNote($"\"{BulkPrompts.Title(prompt)}\" could not be drawn.");
                continue;
            }

            var item = await outputs.SaveAsync(
                context.Source, MediaFunction.BulkStyles, MediaKind.Image, drawn.Data, drawn.ContentType, drawn.Extension, ct,
                title: BulkPrompts.Title(prompt));
            await context.AddOutputAsync(item);
            await context.ReportAsync($"{++made} of {prompts.Count} drawn");
        }

        if (made == 0)
            throw new RunStepException("None of the variations could be drawn. Try again in a minute.");
    }

    /// <summary>
    /// The prompts to draw: the ones sent with the run, else the user's saved set, else the built-in ten.
    /// </summary>
    internal static IReadOnlyList<string> Prompts(IReadOnlyDictionary<string, string> options, IReadOnlyList<string>? savedPrompts)
    {
        var sent = Enumerable.Range(0, BulkPrompts.Count)
            .Select(i => options.GetValueOrDefault(RunOptions.BulkPrompt(i), "").Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return sent.Count > 0 ? sent : savedPrompts is { Count: > 0 } ? savedPrompts : BulkPrompts.All;
    }
}

/// <summary>A user's own set of style prompts: one row per user holding the list as JSON.</summary>
public sealed class BulkPromptRepository(StorageClients storage)
{
    private const string Partition = "prompts";
    private readonly Lazy<TableClient> _table = new(() => storage.Table(StorageNames.Tables.BulkPrompts));

    public async Task<IReadOnlyList<string>?> GetAsync(UserId owner, CancellationToken ct = default)
    {
        var row = await _table.Value.GetEntityIfExistsAsync<TableEntity>(Partition, owner.Key, cancellationToken: ct);
        return row.HasValue ? JsonSerializer.Deserialize<List<string>>(row.Value!.GetString("Prompts") ?? "[]") : null;
    }

    public Task SaveAsync(UserId owner, IReadOnlyList<string> prompts, CancellationToken ct = default) =>
        _table.Value.UpsertEntityAsync(
            new TableEntity(Partition, owner.Key) { ["Prompts"] = JsonSerializer.Serialize(prompts) }, TableUpdateMode.Replace, ct);
}

public static class BulkPromptEndpoints
{
    public static IEndpointRouteBuilder MapBulkPrompts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bulk-prompts").RequireAntiforgeryValidation();
        group.MapGet("/", async (ClaimsPrincipal user, BulkPromptRepository prompts, CancellationToken ct) =>
            TypedResults.Ok((await prompts.GetAsync(UserId.From(user), ct) ?? BulkPrompts.All).ToList()));
        group.MapPut("/", SaveAsync);
        return app;
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SaveAsync(
        List<string> prompts, ClaimsPrincipal user, BulkPromptRepository repository, CancellationToken ct)
    {
        var cleaned = prompts.Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).ToList();
        if (cleaned.Count is 0 or > BulkPrompts.Count || cleaned.Any(p => p.Length > BulkPrompts.MaxLength))
            return TypedResults.Problem(
                detail: $"Save 1 to {BulkPrompts.Count} prompts of up to {BulkPrompts.MaxLength} characters each.",
                statusCode: StatusCodes.Status400BadRequest);

        await repository.SaveAsync(UserId.From(user), cleaned, ct);
        return TypedResults.NoContent();
    }
}
