using System.Text.Json;
using Azure.Data.Tables;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Runs;

/// <summary>Run rows: partition = owner, row = run id.</summary>
public sealed class RunTableRepository(StorageClients storage) : IRunRepository
{
    private readonly Lazy<TableClient> _table = new(() => storage.Table(StorageNames.Tables.Runs));

    public Task SaveAsync(Run run, CancellationToken ct = default) =>
        _table.Value.UpsertEntityAsync(new TableEntity(run.Owner.Key, run.Id.ToString())
        {
            ["Owner"] = run.Owner.Value,
            ["SourceId"] = run.SourceId.ToString(),
            ["Functions"] = string.Join(',', run.Functions),
            ["Options"] = JsonSerializer.Serialize(run.Options),
            ["Status"] = run.Status.ToString(),
            ["CurrentStep"] = run.CurrentStep?.ToString(),
            ["Error"] = run.Error,
            ["OutputIds"] = string.Join(',', run.OutputIds),
            ["Notes"] = JsonSerializer.Serialize(run.Notes),
            ["CreatedAt"] = run.CreatedAt,
        }, TableUpdateMode.Replace, ct);

    public async Task<Run?> GetAsync(UserId owner, RunId id, CancellationToken ct = default)
    {
        var found = await _table.Value.GetEntityIfExistsAsync<TableEntity>(owner.Key, id.ToString(), cancellationToken: ct);
        return found.HasValue ? Map(found.Value!) : null;
    }

    public async Task<IReadOnlyList<Run>> ListAsync(UserId owner, CancellationToken ct = default)
    {
        var runs = new List<Run>();
        await foreach (var row in _table.Value.QueryAsync<TableEntity>(e => e.PartitionKey == owner.Key, cancellationToken: ct))
            runs.Add(Map(row));
        return [.. runs.OrderByDescending(r => r.CreatedAt)];
    }

    private static Run Map(TableEntity row) => new()
    {
        Owner = new UserId(row.GetString("Owner")),
        Id = RunId.Parse(row.RowKey, null),
        SourceId = MediaId.Parse(row.GetString("SourceId"), null),
        Functions = [.. Split(row.GetString("Functions")).Select(Enum.Parse<MediaFunction>)],
        Options = JsonSerializer.Deserialize<Dictionary<string, string>>(row.GetString("Options") ?? "{}") ?? [],
        Status = Enum.Parse<RunStatus>(row.GetString("Status")),
        CurrentStep = row.GetString("CurrentStep") is { } step ? Enum.Parse<MediaFunction>(step) : null,
        Error = row.GetString("Error"),
        OutputIds = [.. Split(row.GetString("OutputIds")).Select(o => MediaId.Parse(o, null))],
        Notes = JsonSerializer.Deserialize<List<string>>(row.GetString("Notes") ?? "[]") ?? [],
        CreatedAt = row.GetDateTimeOffset("CreatedAt") ?? default,
    };

    private static string[] Split(string? list) => (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
}
