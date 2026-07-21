namespace PlanViewer.Core.Models;

/// <summary>
/// The kinds of server-side Query Store filter criteria — one per user-settable predicate.
/// Declaration order is canonical: it is the order active criteria are rendered as chips.
/// <c>EscapeBrackets</c> is deliberately NOT a member: it is a pure modifier on the
/// query-text search, not a standalone criterion.
/// </summary>
public enum ServerFilterKind
{
    MinExecutions,
    MinCpuMs,
    MinDurationMs,
    QueryTextSearch,
    QueryTextSearchNot,
    IncludeQueryIds,
    IgnoreQueryIds,
    IncludePlanIds,
    IgnorePlanIds,
    QueryId,
    PlanId,
    QueryHash,
    QueryPlanHash,
    Module,
    ExecutionType,
}

/// <summary>
/// One active filter criterion projected for display as a removable chip: its
/// <see cref="Kind"/> (used to remove it) and a human-readable <see cref="Label"/>
/// that includes the criterion's value.
/// </summary>
public readonly record struct ServerFilterChip(ServerFilterKind Kind, string Label);

/// <summary>
/// Mutable, UI-facing model of the server Query Store filter. Holds already-parsed
/// criteria (no parsing happens here — callers use <see cref="QueryStoreFilter.ParseIdList"/>
/// and friends first), exposes typed setters that normalize and clear, and projects to
/// ordered chips (for display) and to a <see cref="QueryStoreFilter"/> (for fetching).
/// <para>
/// EscapeBrackets is a pure modifier on the query-text search: it never counts toward
/// <see cref="IsEmpty"/>, never produces its own chip, and is always copied onto any
/// filter <see cref="BuildFilter"/> produces.
/// </para>
/// </summary>
public sealed class QueryStoreServerFilterState
{
    private long? _minExecutions;
    private long? _minCpuMs;
    private long? _minDurationMs;
    private string? _queryTextSearch;
    private string? _queryTextSearchNot;
    private long[]? _includeQueryIds;
    private long[]? _ignoreQueryIds;
    private long[]? _includePlanIds;
    private long[]? _ignorePlanIds;
    private long? _queryId;
    private long? _planId;
    private string? _queryHash;
    private string? _queryPlanHash;
    private string? _module;
    private string[]? _executionType;
    private bool _escapeBrackets;

    // Typed setters. Each normalizes its input and clears the criterion on an "empty" value.

    /// <summary>Sets the minimum executions; null or a non-positive value clears it.</summary>
    public void SetMinExecutions(long? v) => _minExecutions = v is > 0 ? v : null;

    /// <summary>Sets the minimum CPU milliseconds; null or a non-positive value clears it.</summary>
    public void SetMinCpuMs(long? v) => _minCpuMs = v is > 0 ? v : null;

    /// <summary>Sets the minimum duration milliseconds; null or a non-positive value clears it.</summary>
    public void SetMinDurationMs(long? v) => _minDurationMs = v is > 0 ? v : null;

    /// <summary>Sets the query-text search term; null/whitespace clears, otherwise the value is trimmed.</summary>
    public void SetQueryTextSearch(string? v) => _queryTextSearch = Normalize(v);

    /// <summary>Sets the negated query-text search term; null/whitespace clears, otherwise trimmed.</summary>
    public void SetQueryTextSearchNot(string? v) => _queryTextSearchNot = Normalize(v);

    /// <summary>Sets the query hash; null/whitespace clears, otherwise trimmed.</summary>
    public void SetQueryHash(string? v) => _queryHash = Normalize(v);

    /// <summary>Sets the query plan hash; null/whitespace clears, otherwise trimmed.</summary>
    public void SetQueryPlanHash(string? v) => _queryPlanHash = Normalize(v);

    /// <summary>Sets the module name; null/whitespace clears, otherwise trimmed.</summary>
    public void SetModule(string? v) => _module = Normalize(v);

    /// <summary>Sets the single query id; null clears. Any non-null id is kept as-is (no clamp).</summary>
    public void SetQueryId(long? v) => _queryId = v;

    /// <summary>Sets the single plan id; null clears. Any non-null id is kept as-is (no clamp).</summary>
    public void SetPlanId(long? v) => _planId = v;

    /// <summary>Sets the execution-type descriptions; null or an empty array clears.</summary>
    public void SetExecutionType(string[]? v) => _executionType = v is { Length: > 0 } ? v : null;

    /// <summary>Sets the included query ids; null or an empty array clears.</summary>
    public void SetIncludeQueryIds(long[]? ids) => _includeQueryIds = ids is { Length: > 0 } ? ids : null;

    /// <summary>Sets the ignored query ids; null or an empty array clears.</summary>
    public void SetIgnoreQueryIds(long[]? ids) => _ignoreQueryIds = ids is { Length: > 0 } ? ids : null;

    /// <summary>Sets the included plan ids; null or an empty array clears.</summary>
    public void SetIncludePlanIds(long[]? ids) => _includePlanIds = ids is { Length: > 0 } ? ids : null;

    /// <summary>Sets the ignored plan ids; null or an empty array clears.</summary>
    public void SetIgnorePlanIds(long[]? ids) => _ignorePlanIds = ids is { Length: > 0 } ? ids : null;

    /// <summary>
    /// Toggles literal-bracket ("escape") mode for the query-text search. A pure modifier:
    /// excluded from <see cref="IsEmpty"/> and from chips, always copied onto the built filter.
    /// </summary>
    public void SetEscapeBrackets(bool v) => _escapeBrackets = v;

    private static string? Normalize(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>
    /// Clears exactly the given criterion. An inactive criterion, or a kind outside the
    /// enum, is a no-op. Does not touch EscapeBrackets.
    /// </summary>
    public void Remove(ServerFilterKind kind)
    {
        switch (kind)
        {
            case ServerFilterKind.MinExecutions: _minExecutions = null; break;
            case ServerFilterKind.MinCpuMs: _minCpuMs = null; break;
            case ServerFilterKind.MinDurationMs: _minDurationMs = null; break;
            case ServerFilterKind.QueryTextSearch: _queryTextSearch = null; break;
            case ServerFilterKind.QueryTextSearchNot: _queryTextSearchNot = null; break;
            case ServerFilterKind.IncludeQueryIds: _includeQueryIds = null; break;
            case ServerFilterKind.IgnoreQueryIds: _ignoreQueryIds = null; break;
            case ServerFilterKind.IncludePlanIds: _includePlanIds = null; break;
            case ServerFilterKind.IgnorePlanIds: _ignorePlanIds = null; break;
            case ServerFilterKind.QueryId: _queryId = null; break;
            case ServerFilterKind.PlanId: _planId = null; break;
            case ServerFilterKind.QueryHash: _queryHash = null; break;
            case ServerFilterKind.QueryPlanHash: _queryPlanHash = null; break;
            case ServerFilterKind.Module: _module = null; break;
            case ServerFilterKind.ExecutionType: _executionType = null; break;
        }
    }

    /// <summary>Resets every criterion, including the EscapeBrackets modifier.</summary>
    public void Clear()
    {
        _minExecutions = null;
        _minCpuMs = null;
        _minDurationMs = null;
        _queryTextSearch = null;
        _queryTextSearchNot = null;
        _includeQueryIds = null;
        _ignoreQueryIds = null;
        _includePlanIds = null;
        _ignorePlanIds = null;
        _queryId = null;
        _planId = null;
        _queryHash = null;
        _queryPlanHash = null;
        _module = null;
        _executionType = null;
        _escapeBrackets = false;
    }

    /// <summary>True when no criterion is set. The EscapeBrackets modifier is ignored.</summary>
    public bool IsEmpty =>
        _minExecutions is null &&
        _minCpuMs is null &&
        _minDurationMs is null &&
        _queryTextSearch is null &&
        _queryTextSearchNot is null &&
        _includeQueryIds is null &&
        _ignoreQueryIds is null &&
        _includePlanIds is null &&
        _ignorePlanIds is null &&
        _queryId is null &&
        _planId is null &&
        _queryHash is null &&
        _queryPlanHash is null &&
        _module is null &&
        _executionType is null;

    /// <summary>
    /// One chip per active criterion, in <see cref="ServerFilterKind"/> declaration order.
    /// When EscapeBrackets is set, the query-text chip labels carry a literal-mode hint.
    /// </summary>
    public IReadOnlyList<ServerFilterChip> GetActiveChips()
    {
        var chips = new List<ServerFilterChip>();
        if (_minExecutions.HasValue)
            chips.Add(new ServerFilterChip(ServerFilterKind.MinExecutions, $"execs >= {_minExecutions.Value}"));
        if (_minCpuMs.HasValue)
            chips.Add(new ServerFilterChip(ServerFilterKind.MinCpuMs, $"cpu ms >= {_minCpuMs.Value}"));
        if (_minDurationMs.HasValue)
            chips.Add(new ServerFilterChip(ServerFilterKind.MinDurationMs, $"duration ms >= {_minDurationMs.Value}"));
        if (_queryTextSearch is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.QueryTextSearch, $"text: {_queryTextSearch}{EscapeHint()}"));
        if (_queryTextSearchNot is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.QueryTextSearchNot, $"not text: {_queryTextSearchNot}{EscapeHint()}"));
        if (_includeQueryIds is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.IncludeQueryIds, $"query ids: {Join(_includeQueryIds)}"));
        if (_ignoreQueryIds is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.IgnoreQueryIds, $"ignore query ids: {Join(_ignoreQueryIds)}"));
        if (_includePlanIds is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.IncludePlanIds, $"plan ids: {Join(_includePlanIds)}"));
        if (_ignorePlanIds is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.IgnorePlanIds, $"ignore plan ids: {Join(_ignorePlanIds)}"));
        if (_queryId.HasValue)
            chips.Add(new ServerFilterChip(ServerFilterKind.QueryId, $"query id: {_queryId.Value}"));
        if (_planId.HasValue)
            chips.Add(new ServerFilterChip(ServerFilterKind.PlanId, $"plan id: {_planId.Value}"));
        if (_queryHash is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.QueryHash, $"query hash: {_queryHash}"));
        if (_queryPlanHash is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.QueryPlanHash, $"query plan hash: {_queryPlanHash}"));
        if (_module is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.Module, $"module: {_module}"));
        if (_executionType is not null)
            chips.Add(new ServerFilterChip(ServerFilterKind.ExecutionType, $"type: {string.Join(",", _executionType)}"));
        return chips;
    }

    // Literal-mode hint appended to query-text chip labels when escape mode is on.
    private string EscapeHint() => _escapeBrackets ? " (literal [ ] _)" : "";

    private static string Join(long[] ids) => string.Join(",", ids);

    /// <summary>
    /// Projects the active criteria to a <see cref="QueryStoreFilter"/>, or null when
    /// <see cref="IsEmpty"/>. Fields map 1:1 by name, except <c>Module</c> →
    /// <c>ModuleName</c> and <c>ExecutionType</c> → <c>ExecutionTypeDescs</c>. The
    /// EscapeBrackets modifier is always copied onto the result.
    /// </summary>
    public QueryStoreFilter? BuildFilter()
    {
        if (IsEmpty) return null;
        return new QueryStoreFilter
        {
            MinExecutions = _minExecutions,
            MinCpuMs = _minCpuMs,
            MinDurationMs = _minDurationMs,
            QueryTextSearch = _queryTextSearch,
            QueryTextSearchNot = _queryTextSearchNot,
            IncludeQueryIds = _includeQueryIds,
            IgnoreQueryIds = _ignoreQueryIds,
            IncludePlanIds = _includePlanIds,
            IgnorePlanIds = _ignorePlanIds,
            QueryId = _queryId,
            PlanId = _planId,
            QueryHash = _queryHash,
            QueryPlanHash = _queryPlanHash,
            ModuleName = _module,
            ExecutionTypeDescs = _executionType,
            EscapeBrackets = _escapeBrackets,
        };
    }
}
