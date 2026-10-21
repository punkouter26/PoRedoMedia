namespace PoRedoMedia.Api.Features.Runs;

/// <summary>
/// The runs waiting their turn. Owners take turns, so one person queueing many runs cannot hold
/// everyone else up; each owner's own runs still go in the order they were queued.
/// </summary>
public sealed class FairRunQueue : IDisposable
{
    private readonly Lock _lock = new();
    private readonly LinkedList<UserId> _turns = [];
    private readonly Dictionary<UserId, Queue<Run>> _waiting = [];
    private readonly SemaphoreSlim _count = new(0);

    public int Count
    {
        get
        {
            lock (_lock)
                return _waiting.Values.Sum(q => q.Count);
        }
    }

    public void Add(Run run)
    {
        lock (_lock)
        {
            if (!_waiting.TryGetValue(run.Owner, out var queue))
            {
                _waiting[run.Owner] = queue = new Queue<Run>();
                _turns.AddLast(run.Owner);
            }

            queue.Enqueue(run);
        }

        _count.Release();
    }

    public void Dispose() => _count.Dispose();

    /// <summary>The next owner's oldest run. Waits while nothing is queued.</summary>
    public async Task<Run> TakeAsync(CancellationToken ct)
    {
        await _count.WaitAsync(ct);
        lock (_lock)
        {
            var owner = _turns.First!.Value;
            _turns.RemoveFirst();
            var queue = _waiting[owner];
            var run = queue.Dequeue();
            if (queue.Count > 0)
                _turns.AddLast(owner);
            else
                _waiting.Remove(owner);
            return run;
        }
    }
}
