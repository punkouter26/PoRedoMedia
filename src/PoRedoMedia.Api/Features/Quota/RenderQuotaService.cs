using Azure;
using Azure.Data.Tables;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Quota;

/// <summary>
/// Caps runs per user per UTC day.
/// </summary>
/// <remarks>
/// The host is an F1 plan with a hard 60 CPU-minute daily quota for the whole app; once it trips
/// every user gets a 403 until midnight UTC. A per-user allowance is what stops one user from
/// spending everybody's minutes. It resets on the same UTC boundary as the platform quota.
///
/// Counts live in Table Storage because the host recycles after about 20 idle minutes; a counter
/// in memory would hand every user a fresh allowance after each cold start.
///
/// Storage failures fail OPEN: a quota outage should not stop anyone, and storage being down
/// breaks a run anyway.
/// </remarks>
public sealed partial class RenderQuotaService(
    StorageClients storage, IConfiguration configuration, TimeProvider time, ILogger<RenderQuotaService> logger) : IRenderQuota
{
    public const int DefaultDailyLimit = 10;
    private const int MaxConcurrencyRetries = 5;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Quota storage unavailable for {UserId}; allowing the run")]
    private partial void LogStorageFailure(Exception ex, UserId userId);

    private readonly int _dailyLimit = configuration.GetValue<int?>(ConfigKeys.RenderQuotaDailyLimit) ?? DefaultDailyLimit;
    private readonly Lazy<TableClient> _table = new(() => storage.Table(StorageNames.Tables.RenderQuotas));

    public async Task<QuotaStatusDto> GetStatusAsync(UserId userId, CancellationToken ct = default)
    {
        var (day, resetsAt) = CurrentWindow();
        if (_dailyLimit <= 0)
            return new(0, _dailyLimit, resetsAt);

        try
        {
            return new((await GetAsync(day, userId, ct))?.Count ?? 0, _dailyLimit, resetsAt);
        }
        catch (RequestFailedException ex)
        {
            LogStorageFailure(ex, userId);
            return new(0, _dailyLimit, resetsAt);
        }
    }

    public Task<(bool Allowed, QuotaStatusDto Status)> TryConsumeAsync(UserId userId, CancellationToken ct = default) =>
        ChangeAsync(userId, +1, ct);

    /// <summary>Gives back a credit spent on a run that never started.</summary>
    public async Task RefundAsync(UserId userId, CancellationToken ct = default) => await ChangeAsync(userId, -1, ct);

    private async Task<(bool Allowed, QuotaStatusDto Status)> ChangeAsync(UserId userId, int delta, CancellationToken ct)
    {
        var (day, resetsAt) = CurrentWindow();
        if (_dailyLimit <= 0)
            return (true, new(0, _dailyLimit, resetsAt));

        try
        {
            // Optimistic concurrency: two tabs starting at once must not both read N and write
            // N+1. A lost race surfaces as 409 (insert) or 412 (ETag) and simply re-reads.
            for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
            {
                var entity = await GetAsync(day, userId, ct);
                var used = entity?.Count ?? 0;
                if (delta > 0 && used >= _dailyLimit)
                    return (false, new(used, _dailyLimit, resetsAt));

                var next = Math.Max(0, used + delta);
                try
                {
                    if (entity is null)
                    {
                        await _table.Value.AddEntityAsync(new QuotaEntity { PartitionKey = day, RowKey = userId.Key, Count = next }, ct);
                    }
                    else
                    {
                        entity.Count = next;
                        await _table.Value.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
                    }

                    return (true, new(next, _dailyLimit, resetsAt));
                }
                catch (RequestFailedException ex) when (ex.Status is 409 or 412)
                {
                    // Lost the race; loop and re-read.
                }
            }

            // Persistent contention on one user's row: refuse rather than over-spend.
            return (false, await GetStatusAsync(userId, ct));
        }
        catch (RequestFailedException ex)
        {
            LogStorageFailure(ex, userId);
            return (true, new(0, _dailyLimit, resetsAt));
        }
    }

    private (string Day, DateTimeOffset ResetsAt) CurrentWindow()
    {
        var midnight = new DateTimeOffset(time.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        return (midnight.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), midnight.AddDays(1));
    }

    private async Task<QuotaEntity?> GetAsync(string day, UserId userId, CancellationToken ct)
    {
        var response = await _table.Value.GetEntityIfExistsAsync<QuotaEntity>(day, userId.Key, cancellationToken: ct);
        return response.HasValue ? response.Value : null;
    }

    /// <summary>Partition = UTC day (yyyyMMdd). Old days are never read again, so nothing needs cleaning up.</summary>
    private sealed class QuotaEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = "";
        public string RowKey { get; set; } = "";
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public int Count { get; set; }
    }
}
