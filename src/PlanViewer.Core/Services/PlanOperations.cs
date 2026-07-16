using System.Runtime.CompilerServices;
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
    public const long DefaultMaxPlanFileBytes = 16L * 1024 * 1024;
    public const int DefaultMaxSessions = 32;
    public const int DefaultMaxStatements = 10_000;
    public const int DefaultMaxOperators = 100_000;
    public const int DefaultMaxConcurrentOpens = 2;

    private static readonly ConditionalWeakTable<IPlanCatalog, object> CatalogRegistrationGates = new();

    private readonly IPlanCatalog _catalog;
    private readonly AnalyzerConfig _config;
    private readonly SemaphoreSlim _openSlots = new(DefaultMaxConcurrentOpens);

    public PlanOperations(IPlanCatalog catalog, AnalyzerConfig? config = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _config = config ?? ConfigLoader.Load();
    }

    public async Task<PlanSessionSummary> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.GetExtension(path).Equals(".sqlplan", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .sqlplan files can be opened.");

        await _openSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenPathCoreAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _openSlots.Release();
        }
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

        await _openSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenStreamCoreAsync(stream, label, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _openSlots.Release();
        }
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
        EnsureSessionCapacity();
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The plan stream must be readable and seekable.", nameof(stream));
        if (stream.Position != 0)
            throw new ArgumentException("The plan stream must be positioned at the beginning.", nameof(stream));
        if (stream.Length > DefaultMaxPlanFileBytes)
        {
            throw new InvalidDataException(
                $"Plan file exceeds the {DefaultMaxPlanFileBytes / (1024 * 1024)} MiB size limit.");
        }

        var planXml = await ReadPlanXmlAsync(stream, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(planXml))
            throw new InvalidDataException("Plan file is empty.");

        var plan = ShowPlanParser.Parse(planXml);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(plan.ParseError))
            throw new InvalidDataException($"Could not parse plan XML: {plan.ParseError}");
        if (!plan.Batches.SelectMany(batch => batch.Statements).Any())
            throw new InvalidDataException("Could not parse any statements from the plan XML.");

        ValidateComplexity(plan, cancellationToken);
        PlanAnalyzer.Analyze(plan, _config);
        cancellationToken.ThrowIfCancellationRequested();
        BenefitScorer.Score(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var analysis = ResultMapper.Map(plan, label);
        var baseId = CreateBaseSessionId(Path.GetFileNameWithoutExtension(label));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = $"{baseId}-{Guid.NewGuid():N}";
            var session = new PlanSession
            {
                SessionId = sessionId,
                Label = label,
                Source = label,
                Plan = plan,
                StatementCount = analysis.Summary.TotalStatements,
                HasActualStats = analysis.Summary.HasActualStats,
                WarningCount = analysis.Summary.TotalWarnings,
                CriticalWarningCount = analysis.Summary.CriticalWarnings,
                MissingIndexCount = analysis.Summary.MissingIndexes
            };

            if (TryRegisterBounded(session))
                return ToSummary(session);
            EnsureSessionCapacity();
        }
    }

    private static async Task<string> ReadPlanXmlAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream((int)Math.Min(stream.Length, DefaultMaxPlanFileBytes));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                break;
            if (content.Length + bytesRead > DefaultMaxPlanFileBytes)
            {
                throw new InvalidDataException(
                    $"Plan file exceeds the {DefaultMaxPlanFileBytes / (1024 * 1024)} MiB size limit.");
            }

            content.Write(buffer, 0, bytesRead);
        }

        content.Position = 0;
        using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool Close(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _catalog.Unregister(sessionId);
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
        GetMissingIndexes(GetRequiredSession(sessionId));

    public MissingIndexesResult GetMissingIndexes(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var indexes = session.Plan.AllMissingIndexes.Select(index => new MissingIndexItem
        {
            Database = index.Database,
            SchemaName = index.Schema,
            Table = index.Table,
            Impact = index.Impact,
            EqualityColumns = index.EqualityColumns,
            InequalityColumns = index.InequalityColumns,
            IncludeColumns = index.IncludeColumns,
            CreateStatement = index.CreateStatement
        }).ToList();

        return new MissingIndexesResult
        {
            SessionId = session.SessionId,
            MissingIndexCount = indexes.Count,
            Indexes = indexes
        };
    }

    public ExpensiveOperatorsResult GetExpensiveOperators(string sessionId, int top = 10) =>
        GetExpensiveOperators(GetRequiredSession(sessionId), top);

    public ExpensiveOperatorsResult GetExpensiveOperators(
        PlanSession session,
        int top = 10,
        bool useBareObjectNames = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (top is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be between 1 and 100.");

        var analysis = GetAnalysis(session);
        var operators = new List<(OperatorResult Node, string Statement)>();
        foreach (var statement in analysis.Statements)
        {
            if (statement.OperatorTree is not null)
                CollectOperators(statement.OperatorTree, Truncate(statement.StatementText, 100), operators);
        }

        var hasActuals = operators.Any(item => item.Node.ActualElapsedMs > 0);
        var ranked = hasActuals
            ? operators.OrderByDescending(item => item.Node.ActualElapsedMs ?? 0)
            : operators.OrderByDescending(item => item.Node.CostPercent);

        return new ExpensiveOperatorsResult
        {
            SessionId = session.SessionId,
            RankedBy = hasActuals ? "actual_elapsed_ms" : "cost_percent",
            Operators = ranked.Take(top).Select(item => new ExpensiveOperatorItem
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
        GetWarnings(GetRequiredSession(sessionId), severity);

    public PlanWarningsResult GetWarnings(
        PlanSession session,
        string? severity = null,
        bool includeOperatorWarnings = true,
        bool validateSeverity = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (validateSeverity && severity is not null &&
            !new[] { "Critical", "Warning", "Info" }.Contains(severity, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Severity must be Critical, Warning, or Info.", nameof(severity));
        }

        var analysis = GetAnalysis(session);
        var warnings = new List<PlanWarningItem>();
        foreach (var statement in analysis.Statements)
        {
            var statementText = Truncate(statement.StatementText, 200);
            warnings.AddRange(statement.Warnings.Select(warning => ToWarningItem(warning, statementText)));
            if (includeOperatorWarnings && statement.OperatorTree is not null)
                CollectWarnings(statement.OperatorTree, statementText, warnings);
        }

        if (severity is not null)
        {
            warnings = warnings
                .Where(warning => warning.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new PlanWarningsResult
        {
            SessionId = session.SessionId,
            WarningCount = warnings.Count,
            Warnings = warnings
        };
    }

    public AnalysisResult GetAnalysis(string sessionId) =>
        GetAnalysis(GetRequiredSession(sessionId));

    public AnalysisResult GetAnalysis(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ResultMapper.Map(session.Plan, session.Source);
    }

    private bool TryRegisterBounded(PlanSession session)
    {
        var gate = CatalogRegistrationGates.GetValue(_catalog, _ => new object());
        lock (gate)
        {
            if (_catalog.GetAllSessions().Count >= DefaultMaxSessions)
                return false;
            return _catalog.TryRegister(session);
        }
    }

    private void EnsureSessionCapacity()
    {
        if (_catalog.GetAllSessions().Count >= DefaultMaxSessions)
        {
            throw new InvalidOperationException(
                $"The plan session limit of {DefaultMaxSessions} has been reached. Close a session before opening another plan.");
        }
    }

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

    private static void CollectOperators(
        OperatorResult node,
        string statement,
        ICollection<(OperatorResult Node, string Statement)> operators)
    {
        operators.Add((node, statement));
        foreach (var child in node.Children)
            CollectOperators(child, statement, operators);
    }

    private static void CollectWarnings(
        OperatorResult node,
        string statement,
        ICollection<PlanWarningItem> warnings)
    {
        foreach (var warning in node.Warnings)
            warnings.Add(ToWarningItem(warning, statement));
        foreach (var child in node.Children)
            CollectWarnings(child, statement, warnings);
    }

    private static PlanWarningItem ToWarningItem(WarningResult warning, string statement) => new()
    {
        Severity = warning.Severity,
        Type = warning.Type,
        Message = warning.Message,
        NodeId = warning.NodeId,
        Operator = warning.Operator,
        Statement = statement
    };

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
