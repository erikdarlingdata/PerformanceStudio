using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;

namespace PlanViewer.Core.Services;

/// <summary>
/// Typed, renderer-free operations over plans loaded in a shared catalog.
/// </summary>
public sealed class PlanOperations
{
    internal const long DefaultMaxPlanFileBytes = 16L * 1024 * 1024;
    internal const int DefaultMaxSessions = 32;
    internal const int DefaultMaxStatements = 10_000;
    internal const int DefaultMaxOperators = 100_000;
    internal const int DefaultMaxConcurrentOpens = 2;
    internal const int DefaultMaxConcurrentQueries = 4;
    internal const int DefaultMaxWarningResults = 500;
    internal const int DefaultMaxMissingIndexResults = 100;
    internal const int DefaultMaxResponseTextLength = 4_096;

    private readonly IPlanCatalog _catalog;
    private readonly AnalyzerConfig _config;
    private readonly PlanResourceBudget _budget;

    public PlanOperations(IPlanCatalog catalog, AnalyzerConfig? config = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _config = config ?? ConfigLoader.Load();
        _budget = PlanResourceBudget.ForCatalog(_catalog);
    }

    public async Task<PlanSessionSummary> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.GetExtension(path).Equals(".sqlplan", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .sqlplan files can be opened.");

        using var openSlot = await _budget.AcquireOpenSlotAsync(cancellationToken).ConfigureAwait(false);
        return await OpenPathCoreAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlanSessionSummary> OpenAsync(
        FileStream stream,
        string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (!Path.GetExtension(label).Equals(".sqlplan", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(label).Equals(label, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The plan label must be a .sqlplan file name.");
        }

        using var openSlot = await _budget.AcquireOpenSlotAsync(cancellationToken).ConfigureAwait(false);
        return await OpenStreamCoreAsync(stream, label, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PlanSessionSummary> OpenOwnedStreamAsync(
        Func<CancellationToken, ValueTask<(FileStream Stream, string Label, IAsyncDisposable Owner)>> streamFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        using var openSlot = await _budget.AcquireOpenSlotAsync(cancellationToken).ConfigureAwait(false);
        var source = await streamFactory(cancellationToken).ConfigureAwait(false);
        await using var owner = source.Owner.ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(source.Stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Label);
        if (!Path.GetExtension(source.Label).Equals(".sqlplan", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(source.Label).Equals(source.Label, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The plan label must be a .sqlplan file name.");
        }

        return await OpenStreamCoreAsync(source.Stream, source.Label, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlanSessionSummary> OpenPathCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await OpenStreamCoreAsync(
            stream,
            Path.GetFileName(fullPath),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlanSessionSummary> OpenStreamCoreAsync(
        FileStream stream,
        string label,
        CancellationToken cancellationToken)
    {
        _budget.EnsureSessionCapacity(DefaultMaxSessions);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The plan stream must be readable and seekable.", nameof(stream));
        if (stream.Position != 0)
            throw new ArgumentException("The plan stream must be positioned at the beginning.", nameof(stream));
        if (stream.Length > DefaultMaxPlanFileBytes)
        {
            throw new InvalidDataException(
                $"Plan file exceeds the {DefaultMaxPlanFileBytes / (1024 * 1024)} MiB size limit.");
        }

        var preflight = await PlanXmlPreflight.ValidateAsync(stream, cancellationToken).ConfigureAwait(false);
        using var retainedEstimate = _budget.ReserveRetainedEstimate(
            EstimateRetainedAnalysisBytes(stream.Length, preflight));
        var decodedPlan = await ReadPlanXmlAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(preflight.ContentHash, decodedPlan.ContentHash))
            throw new InvalidDataException("Plan file changed while it was being read.");
        cancellationToken.ThrowIfCancellationRequested();
        var planXml = decodedPlan.Content;
        if (string.IsNullOrWhiteSpace(planXml))
            throw new InvalidDataException("Plan file is empty.");

        var plan = await PlanAnalysisPipeline.AnalyzeAsync(
            planXml,
            _config,
            serverMetadata: null,
            cancellationToken,
            ValidateComplexity).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(plan.ParseError))
            throw new InvalidDataException($"Could not parse plan XML: {plan.ParseError}");
        if (!plan.Batches.SelectMany(batch => batch.Statements).Any())
            throw new InvalidDataException("Could not parse any statements from the plan XML.");

        var analysis = ResultMapper.MapCancellable(plan, label, metadata: null, cancellationToken);
        plan.RawXml = string.Empty;
        plan.Batches.Clear();
        var baseId = CreateBaseSessionId(Path.GetFileNameWithoutExtension(label));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var opaqueId = Guid.NewGuid().ToString("N")[..12];
            var sessionId = $"{baseId}-{opaqueId}";
            var session = new PlanSession
            {
                SessionId = sessionId,
                Label = label,
                Source = label,
                Plan = plan,
                Analysis = analysis,
                StatementCount = analysis.Summary.TotalStatements,
                HasActualStats = analysis.Summary.HasActualStats,
                WarningCount = analysis.Summary.TotalWarnings,
                CriticalWarningCount = analysis.Summary.CriticalWarnings,
                MissingIndexCount = analysis.Summary.MissingIndexes
            };

            if (_budget.TryRegister(session, DefaultMaxSessions))
            {
                retainedEstimate.Commit(sessionId);
                return ToSummary(session);
            }
            _budget.EnsureSessionCapacity(DefaultMaxSessions);
        }
    }

    private static long EstimateRetainedAnalysisBytes(
        long sourceBytes,
        PlanXmlPreflightResult preflight) =>
        checked(
            (sourceBytes * 2) +
            ((long)preflight.StatementCount * 2 * 1024) +
            ((long)preflight.OperatorCount * 4 * 1024) +
            ((long)preflight.ElementCount * 256) +
            ((long)preflight.AttributeCount * 128));

    private static async Task<DecodedPlan> ReadPlanXmlAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var sha256 = SHA256.Create();
        await using var hashingStream = new CryptoStream(
            stream,
            sha256,
            CryptoStreamMode.Read,
            leaveOpen: true);
        using var reader = new StreamReader(
            hashingStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: true);
        var content = new StringBuilder((int)Math.Min(stream.Length, DefaultMaxPlanFileBytes));
        var buffer = ArrayPool<char>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var charactersRead = await reader
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (charactersRead == 0)
                    break;
                if (stream.Position > DefaultMaxPlanFileBytes ||
                    content.Length + charactersRead > DefaultMaxPlanFileBytes)
                {
                    throw new InvalidDataException(
                        $"Plan file exceeds the {DefaultMaxPlanFileBytes / (1024 * 1024)} MiB size limit.");
                }

                content.Append(buffer, 0, charactersRead);
            }

            return new DecodedPlan(
                content.ToString(),
                sha256.Hash?.ToArray()
                    ?? throw new InvalidDataException("Could not hash the decoded plan XML."));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private readonly record struct DecodedPlan(string Content, byte[] ContentHash);

    public bool Close(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _budget.TryUnregister(sessionId);
    }

    public PlanSummaryResult GetSummary(string sessionId) =>
        GetSummary(GetRequiredSession(sessionId));

    public PlanSummaryResult GetSummary(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var analysis = GetAnalysis(session);
        return new PlanSummaryResult
        {
            SessionId = session.SessionId,
            Label = session.Label,
            Source = session.Source,
            TotalStatements = analysis.Summary.TotalStatements,
            TotalWarnings = analysis.Summary.TotalWarnings,
            CriticalWarnings = analysis.Summary.CriticalWarnings,
            MissingIndexes = analysis.Summary.MissingIndexes,
            HasActualStats = analysis.Summary.HasActualStats,
            MaxEstimatedCost = analysis.Summary.MaxEstimatedCost,
            WarningTypes = analysis.Summary.WarningTypes
        };
    }

    public MissingIndexesResult GetMissingIndexes(string sessionId) =>
        GetMissingIndexes(sessionId, CancellationToken.None);

    internal MissingIndexesResult GetMissingIndexes(
        string sessionId,
        CancellationToken cancellationToken) =>
        GetMissingIndexes(GetRequiredSession(sessionId), cancellationToken);

    public MissingIndexesResult GetMissingIndexes(PlanSession session) =>
        GetMissingIndexes(session, CancellationToken.None);

    internal MissingIndexesResult GetMissingIndexes(
        PlanSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var querySlot = _budget.AcquireQuerySlot(cancellationToken);
        var indexes = new List<MissingIndexItem>(DefaultMaxMissingIndexResults);
        var totalIndexCount = 0;
        foreach (var statement in GetAnalysisCancellable(session, cancellationToken).Statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var index in statement.MissingIndexes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalIndexCount++;
                if (indexes.Count >= DefaultMaxMissingIndexResults)
                    continue;
                indexes.Add(new MissingIndexItem
                {
                    Database = Truncate(index.Database, 512),
                    SchemaName = Truncate(index.SchemaName, 512),
                    Table = Truncate(index.BareTable, 512),
                    Impact = index.Impact,
                    EqualityColumns = BoundColumns(index.EqualityColumns),
                    InequalityColumns = BoundColumns(index.InequalityColumns),
                    IncludeColumns = BoundColumns(index.IncludeColumns),
                    CreateStatement = Truncate(index.CreateStatement, DefaultMaxResponseTextLength)
                });
            }
        }

        return new MissingIndexesResult
        {
            SessionId = session.SessionId,
            MissingIndexCount = totalIndexCount,
            ReturnedIndexCount = indexes.Count,
            Truncated = indexes.Count < totalIndexCount,
            Indexes = indexes
        };
    }

    public ExpensiveOperatorsResult GetExpensiveOperators(string sessionId, int top = 10) =>
        GetExpensiveOperators(sessionId, top, CancellationToken.None);

    internal ExpensiveOperatorsResult GetExpensiveOperators(
        string sessionId,
        int top,
        CancellationToken cancellationToken) =>
        GetExpensiveOperators(GetRequiredSession(sessionId), top, useBareObjectNames: false, cancellationToken);

    public ExpensiveOperatorsResult GetExpensiveOperators(
        PlanSession session,
        int top = 10,
        bool useBareObjectNames = false) =>
        GetExpensiveOperators(session, top, useBareObjectNames, CancellationToken.None);

    internal ExpensiveOperatorsResult GetExpensiveOperators(
        PlanSession session,
        int top,
        CancellationToken cancellationToken) =>
        GetExpensiveOperators(session, top, useBareObjectNames: false, cancellationToken);

    internal ExpensiveOperatorsResult GetExpensiveOperators(
        PlanSession session,
        int top,
        bool useBareObjectNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (top is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be between 1 and 100.");

        using var querySlot = _budget.AcquireQuerySlot(cancellationToken);
        var analysis = GetAnalysisCancellable(session, cancellationToken);
        var topByActual = new List<RankedOperator>(top);
        var topByCost = new List<RankedOperator>(top);
        var hasActuals = false;
        foreach (var statement in analysis.Statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement.OperatorTree is null)
                continue;
            var statementText = Truncate(statement.StatementText, 100);
            VisitOperators(statement.OperatorTree, cancellationToken, node =>
            {
                var candidate = new RankedOperator(node, statementText);
                hasActuals |= node.ActualElapsedMs > 0;
                AddRanked(topByActual, candidate, top, rankByActuals: true);
                AddRanked(topByCost, candidate, top, rankByActuals: false);
            });
        }

        var ranked = hasActuals ? topByActual : topByCost;
        return new ExpensiveOperatorsResult
        {
            SessionId = session.SessionId,
            RankedBy = hasActuals ? "actual_elapsed_ms" : "cost_percent",
            Operators = ranked.Select(item => new ExpensiveOperatorItem
            {
                NodeId = item.Node.NodeId,
                PhysicalOp = item.Node.PhysicalOp,
                LogicalOp = item.Node.LogicalOp,
                CostPercent = item.Node.CostPercent,
                EstimatedRows = item.Node.EstimatedRows,
                ActualRows = item.Node.ActualRows ?? 0,
                ActualElapsedMs = item.Node.ActualElapsedMs ?? 0,
                ActualCpuMs = item.Node.ActualCpuMs ?? 0,
                LogicalReads = item.Node.ActualLogicalReads ?? 0,
                PhysicalReads = item.Node.ActualPhysicalReads ?? 0,
                ObjectName = useBareObjectNames ? item.Node.BareObjectName : item.Node.ObjectName,
                Statement = item.Statement
            }).ToList()
        };
    }

    public PlanWarningsResult GetWarnings(string sessionId, string? severity = null) =>
        GetWarnings(sessionId, severity, CancellationToken.None);

    internal PlanWarningsResult GetWarnings(
        string sessionId,
        string? severity,
        CancellationToken cancellationToken) =>
        GetWarnings(GetRequiredSession(sessionId), severity, includeOperatorWarnings: true, validateSeverity: true, cancellationToken);

    public PlanWarningsResult GetWarnings(
        PlanSession session,
        string? severity = null,
        bool includeOperatorWarnings = true,
        bool validateSeverity = true) =>
        GetWarnings(session, severity, includeOperatorWarnings, validateSeverity, CancellationToken.None);

    internal PlanWarningsResult GetWarnings(
        PlanSession session,
        string? severity,
        CancellationToken cancellationToken) =>
        GetWarnings(session, severity, includeOperatorWarnings: true, validateSeverity: true, cancellationToken);

    internal PlanWarningsResult GetWarnings(
        PlanSession session,
        string? severity,
        bool includeOperatorWarnings,
        bool validateSeverity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (validateSeverity && severity is not null &&
            !new[] { "Critical", "Warning", "Info" }.Contains(severity, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Severity must be Critical, Warning, or Info.", nameof(severity));
        }

        using var querySlot = _budget.AcquireQuerySlot(cancellationToken);
        var analysis = GetAnalysisCancellable(session, cancellationToken);
        var returnedWarnings = new List<PlanWarningItem>(DefaultMaxWarningResults);
        var totalWarningCount = 0;
        foreach (var statement in analysis.Statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statementText = Truncate(statement.StatementText, 200);
            foreach (var warning in statement.Warnings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddWarning(warning, statementText);
            }
            if (includeOperatorWarnings && statement.OperatorTree is not null)
            {
                VisitOperators(statement.OperatorTree, cancellationToken, node =>
                {
                    foreach (var warning in node.Warnings)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AddWarning(warning, statementText);
                    }
                });
            }
        }

        return new PlanWarningsResult
        {
            SessionId = session.SessionId,
            WarningCount = totalWarningCount,
            ReturnedWarningCount = returnedWarnings.Count,
            Truncated = returnedWarnings.Count < totalWarningCount,
            Warnings = returnedWarnings
        };

        void AddWarning(WarningResult warning, string statementText)
        {
            if (severity is not null &&
                !warning.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            totalWarningCount++;
            if (returnedWarnings.Count < DefaultMaxWarningResults)
                returnedWarnings.Add(ToWarningItem(warning, statementText));
        }
    }

    internal IDisposable AcquireQueryScope(CancellationToken cancellationToken) =>
        _budget.AcquireQuerySlot(cancellationToken);

    internal AnalysisResult GetAnalysisForRequest(
        PlanSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        return GetAnalysisCancellable(session, cancellationToken);
    }

    public AnalysisResult GetAnalysis(string sessionId) =>
        GetAnalysis(GetRequiredSession(sessionId));

    public AnalysisResult GetAnalysis(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Analysis ?? ResultMapper.Map(session.Plan, session.Source);
    }

    private static AnalysisResult GetAnalysisCancellable(
        PlanSession session,
        CancellationToken cancellationToken) =>
        session.Analysis ?? ResultMapper.MapCancellable(session.Plan, session.Source, metadata: null, cancellationToken);

    private static void ValidateComplexity(ParsedPlan plan, CancellationToken cancellationToken)
    {
        var pendingStatements = new Stack<PlanStatement>(
            plan.Batches.SelectMany(batch => batch.Statements).Reverse());
        var pendingOperators = new Stack<PlanNode>();
        var statements = 0;
        while (pendingStatements.TryPop(out var statement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            statements++;
            if (statements > DefaultMaxStatements)
            {
                throw new InvalidDataException(
                    $"Plan exceeds the {DefaultMaxStatements} statement complexity limit.");
            }

            if (statement.RootNode is not null)
                pendingOperators.Push(statement.RootNode);
            if (statement.StoredProcPlan is not null)
                PushStatements(statement.StoredProcPlan.Statements, pendingStatements);
            for (var index = statement.UdfPlans.Count - 1; index >= 0; index--)
                PushStatements(statement.UdfPlans[index].Statements, pendingStatements);
        }

        var operators = 0;
        while (pendingOperators.TryPop(out var node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            operators++;
            if (operators > DefaultMaxOperators)
            {
                throw new InvalidDataException(
                    $"Plan exceeds the {DefaultMaxOperators} operator complexity limit.");
            }

            foreach (var child in node.Children)
                pendingOperators.Push(child);
        }
    }

    private static void PushStatements(
        IReadOnlyList<PlanStatement> statements,
        Stack<PlanStatement> pending)
    {
        for (var index = statements.Count - 1; index >= 0; index--)
            pending.Push(statements[index]);
    }

    private readonly record struct RankedOperator(OperatorResult Node, string Statement);

    private static void VisitOperators(
        OperatorResult node,
        CancellationToken cancellationToken,
        Action<OperatorResult> visit)
    {
        var pending = new Stack<OperatorResult>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visit(current);
            for (var index = current.Children.Count - 1; index >= 0; index--)
                pending.Push(current.Children[index]);
        }
    }

    private static void AddRanked(
        List<RankedOperator> ranked,
        RankedOperator candidate,
        int maximum,
        bool rankByActuals)
    {
        var candidateScore = rankByActuals
            ? candidate.Node.ActualElapsedMs ?? 0
            : candidate.Node.CostPercent;
        var insertAt = 0;
        while (insertAt < ranked.Count)
        {
            var item = ranked[insertAt];
            var score = rankByActuals ? item.Node.ActualElapsedMs ?? 0 : item.Node.CostPercent;
            if (candidateScore > score)
            {
                break;
            }
            insertAt++;
        }
        if (insertAt >= maximum)
            return;
        ranked.Insert(insertAt, candidate);
        if (ranked.Count > maximum)
            ranked.RemoveAt(maximum);
    }

    private static PlanWarningItem ToWarningItem(WarningResult warning, string statement) => new()
    {
        Severity = Truncate(warning.Severity, 32),
        Type = Truncate(warning.Type, 256),
        Message = Truncate(warning.Message, DefaultMaxResponseTextLength),
        NodeId = warning.NodeId,
        Operator = warning.Operator is null ? null : Truncate(warning.Operator, 512),
        Statement = statement
    };

    private static IReadOnlyList<string> BoundColumns(IEnumerable<string> columns) =>
        columns.Take(64).Select(column => Truncate(column, 512)).ToList();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "... (truncated)";

    private PlanSession GetRequiredSession(string sessionId) =>
        _catalog.GetSession(sessionId)
        ?? throw new KeyNotFoundException($"Plan session {sessionId} was not found.");

    private static string CreateBaseSessionId(string label)
    {
        var baseId = Regex.Replace(label.Trim(), "[^A-Za-z0-9._-]+", "-").Trim('-');
        if (baseId.Length == 0)
            return "plan";
        if (baseId.Length > 48)
            baseId = baseId[..48].TrimEnd('.', '_', '-');
        if (baseId.Length == 0)
            return "plan";
        return baseId.Equals("open", StringComparison.OrdinalIgnoreCase) ||
               baseId.Equals("list", StringComparison.OrdinalIgnoreCase)
            ? $"plan-{baseId}"
            : baseId;
    }

    private static PlanSessionSummary ToSummary(PlanSession session) => new()
    {
        SessionId = session.SessionId,
        Label = session.Label,
        Source = session.Source,
        StatementCount = session.StatementCount,
        WarningCount = session.WarningCount,
        CriticalWarningCount = session.CriticalWarningCount,
        MissingIndexCount = session.MissingIndexCount,
        HasActualStats = session.HasActualStats
    };
}
