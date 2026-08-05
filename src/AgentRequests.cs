namespace Revue;

/// <summary>
/// In-memory queue of "address these comments" requests raised from the browser
/// and claimed by an attached Copilot session that is long-polling
/// <c>/api/agent/wait</c>. Requests are deliberately not persisted: they only
/// mean anything while both this server and the CLI session that launched it are
/// alive.
/// </summary>
public sealed class AgentRequestQueue
{
    // Keep a bounded tail of finished requests per repo so the UI can show the
    // outcome of the last round without growing without bound.
    private const int HistoryLimit = 20;

    // Windows paths are case-insensitive; POSIX paths are not.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public const string StatusPending = "pending";
    public const string StatusWorking = "working";
    public const string StatusDone = "done";

    private readonly object _lock = new();
    private readonly List<AgentRequest> _requests = [];
    private readonly Dictionary<string, int> _waiters = new(PathComparer);
    private readonly Dictionary<string, TaskCompletionSource> _signals = new(PathComparer);

    /// <summary>Queues a request and wakes any session waiting on that repo.</summary>
    public AgentRequest Enqueue(string repo, string? branch, string? note, IEnumerable<string> commentIds)
    {
        var request = new AgentRequest(
            Id: Guid.NewGuid().ToString(),
            Repo: repo,
            Branch: branch,
            Note: note ?? "",
            CommentIds: [.. commentIds],
            Created: DateTime.UtcNow.ToString("o"),
            Status: StatusPending,
            Claimed: null,
            Completed: null,
            Summary: null);

        TaskCompletionSource? signal;
        lock (_lock)
        {
            _requests.Add(request);
            TrimLocked(repo);
            signal = TakeSignalLocked(repo);
        }
        signal?.TrySetResult();
        return request;
    }

    /// <summary>
    /// Blocks until a pending request for <paramref name="repo"/> can be claimed,
    /// flipping it to <c>working</c>, or returns null once <paramref name="timeout"/>
    /// elapses. Throws <see cref="OperationCanceledException"/> when the caller
    /// disconnects so an abandoned long-poll doesn't leave a phantom waiter.
    /// </summary>
    public async Task<AgentRequest?> WaitAsync(string repo, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        AddWaiter(repo, 1);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                Task signal;
                lock (_lock)
                {
                    var claimed = TryClaimLocked(repo);
                    if (claimed is not null) return claimed;
                    signal = EnsureSignalLocked(repo).Task;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return null;

                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(remaining, delayCts.Token);
                var finished = await Task.WhenAny(signal, delay).ConfigureAwait(false);
                delayCts.Cancel(); // stop the timer whichever way we got here

                ct.ThrowIfCancellationRequested();
                if (finished == delay) return null;
            }
        }
        finally
        {
            AddWaiter(repo, -1);
        }
    }

    /// <summary>Marks a claimed request done. Returns null if the id is unknown.</summary>
    public AgentRequest? Complete(string id, string? summary)
    {
        lock (_lock)
        {
            var idx = _requests.FindIndex(r => r.Id == id);
            if (idx < 0) return null;
            _requests[idx] = _requests[idx] with
            {
                Status = StatusDone,
                Completed = DateTime.UtcNow.ToString("o"),
                Summary = string.IsNullOrWhiteSpace(summary) ? null : summary,
            };
            return _requests[idx];
        }
    }

    /// <summary>
    /// Drops a request entirely. The UI's escape hatch for a round whose session
    /// died before completing it, which would otherwise sit in <c>working</c> forever.
    /// </summary>
    public bool Cancel(string id)
    {
        lock (_lock) { return _requests.RemoveAll(r => r.Id == id) > 0; }
    }

    /// <summary>Repo path owning <paramref name="id"/>, so repo-less calls can find it.</summary>
    public string? RepoForRequest(string id)
    {
        lock (_lock) { return _requests.FirstOrDefault(r => r.Id == id)?.Repo; }
    }

    /// <summary>Queue snapshot for a repo, as shown in the UI's agent panel.</summary>
    public AgentStatusInfo Status(string repo)
    {
        lock (_lock)
        {
            var mine = _requests.Where(r => PathComparer.Equals(r.Repo, repo)).ToList();
            var waiters = _waiters.TryGetValue(repo, out var w) ? w : 0;
            return new AgentStatusInfo(
                Attached: waiters > 0,
                Waiters: waiters,
                Pending: mine.Count(r => r.Status == StatusPending),
                Queued: mine.FirstOrDefault(r => r.Status == StatusPending),
                Active: mine.LastOrDefault(r => r.Status == StatusWorking),
                Last: mine.LastOrDefault(r => r.Status == StatusDone));
        }
    }

    // Caller must hold _lock.
    private AgentRequest? TryClaimLocked(string repo)
    {
        var idx = _requests.FindIndex(r => r.Status == StatusPending && PathComparer.Equals(r.Repo, repo));
        if (idx < 0) return null;
        _requests[idx] = _requests[idx] with
        {
            Status = StatusWorking,
            Claimed = DateTime.UtcNow.ToString("o"),
        };
        return _requests[idx];
    }

    // Caller must hold _lock. Unfinished requests are never evicted.
    private void TrimLocked(string repo)
    {
        var mine = _requests.Where(r => PathComparer.Equals(r.Repo, repo)).ToList();
        var excess = mine.Count - HistoryLimit;
        foreach (var r in mine)
        {
            if (excess <= 0) break;
            if (r.Status != StatusDone) continue;
            _requests.Remove(r);
            excess--;
        }
    }

    // Caller must hold _lock. Continuations run off-thread so a waiter can't
    // resume inside the lock held by the enqueuing request.
    private TaskCompletionSource EnsureSignalLocked(string repo)
    {
        if (!_signals.TryGetValue(repo, out var tcs))
        {
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _signals[repo] = tcs;
        }
        return tcs;
    }

    // Caller must hold _lock.
    private TaskCompletionSource? TakeSignalLocked(string repo)
    {
        if (!_signals.Remove(repo, out var tcs)) return null;
        return tcs;
    }

    private void AddWaiter(string repo, int delta)
    {
        lock (_lock)
        {
            var count = (_waiters.TryGetValue(repo, out var w) ? w : 0) + delta;
            if (count <= 0) _waiters.Remove(repo);
            else _waiters[repo] = count;
        }
    }
}
