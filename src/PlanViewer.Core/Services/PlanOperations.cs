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
    private readonly IPlanCatalog _catalog;
    private readonly AnalyzerConfig _config;

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

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Plan file not found: {fullPath}", fullPath);

        var planXml = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(planXml))
            throw new InvalidDataException("Plan file is empty.");

        var plan = ShowPlanParser.Parse(planXml);
        PlanAnalyzer.Analyze(plan, _config);
        BenefitScorer.Score(plan);

        if (plan.Batches.SelectMany(batch => batch.Statements).Count() == 0)
            throw new InvalidDataException("Could not parse any statements from the plan XML.");

        var label = Path.GetFileName(fullPath);
        var analysis = ResultMapper.Map(plan, label);
        var baseId = CreateBaseSessionId(Path.GetFileNameWithoutExtension(fullPath));
        for (var suffix = 1; ; suffix++)
        {
            var sessionId = suffix == 1 ? baseId : $"{baseId}-{suffix}";
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

            if (_catalog.TryRegister(session))
                return ToSummary(session);
        }
    }

    public PlanSummaryResult GetSummary(string sessionId)
    {
        var session = GetRequiredSession(sessionId);
        var analysis = ResultMapper.Map(session.Plan, session.Source);
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

    public MissingIndexesResult GetMissingIndexes(string sessionId)
    {
        var session = GetRequiredSession(sessionId);
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
            SessionId = sessionId,
            MissingIndexCount = indexes.Count,
            Indexes = indexes
        };
    }

    public ExpensiveOperatorsResult GetExpensiveOperators(string sessionId, int top = 10)
    {
        if (top is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be between 1 and 100.");

        var analysis = GetAnalysis(sessionId);
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
            SessionId = sessionId,
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
                ObjectName = item.Node.ObjectName,
                Statement = item.Statement
            }).ToList()
        };
    }

    public PlanWarningsResult GetWarnings(string sessionId, string? severity = null)
    {
        if (severity is not null &&
            !new[] { "Critical", "Warning", "Info" }.Contains(severity, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Severity must be Critical, Warning, or Info.", nameof(severity));
        }

        var analysis = GetAnalysis(sessionId);
        var warnings = new List<PlanWarningItem>();
        foreach (var statement in analysis.Statements)
        {
            var statementText = Truncate(statement.StatementText, 200);
            warnings.AddRange(statement.Warnings.Select(warning => ToWarningItem(warning, statementText)));
            if (statement.OperatorTree is not null)
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
            SessionId = sessionId,
            WarningCount = warnings.Count,
            Warnings = warnings
        };
    }

    public AnalysisResult GetAnalysis(string sessionId)
    {
        var session = GetRequiredSession(sessionId);
        return ResultMapper.Map(session.Plan, session.Source);
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
        var baseId = Regex.Replace(label.Trim(), "[^A-Za-z0-9._-]+", "-").Trim("-".ToCharArray());
        return baseId.Length == 0 ? "plan" : baseId;
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
