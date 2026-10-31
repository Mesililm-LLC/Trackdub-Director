namespace Trackdub.Director.Core;

/// <summary>
/// Counts the Meta HTTP requests made for one run and refuses the next one once the
/// limit is reached. Clients made with <see cref="MetaClient.WithBudget"/> charge every
/// request they send here, including requests made inside QA.
/// </summary>
public sealed class CallBudget
{
    private readonly object _gate = new();
    private readonly int _limit;
    private int _used;

    public CallBudget(int limit)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The call limit cannot be negative.");
        }

        _limit = limit;
    }

    public int Limit => _limit;

    public int Used
    {
        get
        {
            lock (_gate)
            {
                return _used;
            }
        }
    }

    public int Remaining
    {
        get
        {
            lock (_gate)
            {
                return _limit - _used;
            }
        }
    }

    /// <summary>Charges one request, or throws when none is left.</summary>
    public void Reserve()
    {
        lock (_gate)
        {
            if (_used >= _limit)
            {
                throw new MetaBudgetExhaustedException(_limit);
            }

            _used++;
        }
    }
}

/// <summary>Raised before a request is sent when the run's Meta call budget is spent.</summary>
public sealed class MetaBudgetExhaustedException : MetaApiException
{
    public MetaBudgetExhaustedException(int limit)
        : base($"Meta call budget of {limit} requests is exhausted for this run.")
    {
    }
}
