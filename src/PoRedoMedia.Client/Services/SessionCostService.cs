namespace PoRedoMedia.Client.Services;

/// <summary>A running estimate of what this browser session's runs have cost, from list prices.</summary>
public sealed class SessionCostService
{
    public decimal Total { get; private set; }

    public event Action? Changed;

    public void Add(decimal estimate)
    {
        Total += estimate;
        Changed?.Invoke();
    }
}
