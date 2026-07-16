using System.Runtime.CompilerServices;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

internal sealed class PlanResourceBudget
{
    internal const long DefaultMaxEstimatedRetainedBytes = 64L * 1024 * 1024;

    private static readonly ConditionalWeakTable<IPlanCatalog, PlanResourceBudget> Budgets = new();

    private readonly IPlanCatalog _catalog;
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _openSlots = new(PlanOperations.DefaultMaxConcurrentOpens);
    private readonly SemaphoreSlim _querySlots = new(PlanOperations.DefaultMaxConcurrentQueries);
    private readonly Dictionary<string, long> _retainedEstimateBySession = new(StringComparer.OrdinalIgnoreCase);
    private long _reservedAndRetainedEstimate;

    private PlanResourceBudget(IPlanCatalog catalog)
    {
        _catalog = catalog;
    }

    internal static PlanResourceBudget ForCatalog(IPlanCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Budgets.GetValue(catalog, static value => new PlanResourceBudget(value));
    }

    internal async Task<IDisposable> AcquireOpenSlotAsync(CancellationToken cancellationToken)
    {
        if (!await _openSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The concurrent plan-open limit of {PlanOperations.DefaultMaxConcurrentOpens} has been reached. Retry after an active open completes.");
        }

        return new SemaphoreLease(_openSlots);
    }

    internal IDisposable AcquireQuerySlot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_querySlots.Wait(0, cancellationToken))
        {
            throw new InvalidOperationException(
                $"The concurrent plan-query limit of {PlanOperations.DefaultMaxConcurrentQueries} has been reached. Retry after an active query completes.");
        }

        return new SemaphoreLease(_querySlots);
    }

    internal bool TryRegister(PlanSession session, int maxSessions)
    {
        lock (_syncRoot)
        {
            if (_catalog.GetAllSessions().Count >= maxSessions)
            {
                throw new InvalidOperationException(
                    $"The plan session limit of {maxSessions} has been reached. Close a session before opening another plan.");
            }

            return _catalog.TryRegister(session);
        }
    }

    internal void EnsureSessionCapacity(int maxSessions)
    {
        lock (_syncRoot)
        {
            if (_catalog.GetAllSessions().Count >= maxSessions)
            {
                throw new InvalidOperationException(
                    $"The plan session limit of {maxSessions} has been reached. Close a session before opening another plan.");
            }
        }
    }

    internal bool TryUnregister(string sessionId)
    {
        lock (_syncRoot)
        {
            if (!_catalog.Unregister(sessionId))
                return false;
            if (_retainedEstimateBySession.Remove(sessionId, out var bytes))
                _reservedAndRetainedEstimate -= bytes;
            return true;
        }
    }

    internal RetainedEstimateReservation ReserveRetainedEstimate(long bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        lock (_syncRoot)
        {
            if (bytes > DefaultMaxEstimatedRetainedBytes - _reservedAndRetainedEstimate)
            {
                throw new InvalidOperationException(
                    $"Opening this plan would exceed the aggregate {DefaultMaxEstimatedRetainedBytes / (1024 * 1024)} MiB retained-analysis estimate budget. Close a session before opening another plan.");
            }

            _reservedAndRetainedEstimate += bytes;
            return new RetainedEstimateReservation(this, bytes);
        }
    }

    private void Commit(RetainedEstimateReservation reservation, string sessionId)
    {
        lock (_syncRoot)
        {
            if (!_retainedEstimateBySession.TryAdd(sessionId, reservation.Bytes))
                throw new InvalidOperationException($"A retained-byte reservation already exists for session {sessionId}.");
            reservation.MarkCommitted();
        }
    }

    private void ReleaseReservation(long bytes)
    {
        lock (_syncRoot)
            _reservedAndRetainedEstimate -= bytes;
    }

    internal sealed class RetainedEstimateReservation : IDisposable
    {
        private PlanResourceBudget? _owner;
        private bool _committed;

        internal RetainedEstimateReservation(PlanResourceBudget owner, long bytes)
        {
            _owner = owner;
            Bytes = bytes;
        }

        internal long Bytes { get; }

        internal void Commit(string sessionId)
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(RetainedEstimateReservation));
            owner.Commit(this, sessionId);
        }

        internal void MarkCommitted() => _committed = true;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null && !_committed)
                owner.ReleaseReservation(Bytes);
        }
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
