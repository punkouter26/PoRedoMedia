namespace PoRedoMedia.Shared.Models;

/// <summary>The caller's run allowance for the current UTC day.</summary>
public sealed record QuotaStatusDto(int Used, int Limit, DateTimeOffset ResetsAt)
{
    /// <summary>A non-positive limit means quotas are switched off.</summary>
    public bool Unlimited => Limit <= 0;

    public int Remaining => Unlimited ? int.MaxValue : Math.Max(0, Limit - Used);
}
