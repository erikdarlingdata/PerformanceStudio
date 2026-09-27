using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Gates that decide whether an analyzer rule fires at all: #577 (rule 5 on operators that never
/// executed), #576 (rule 6 suppressed on the strength of a rule 3 finding that never appeared),
/// #579 (hints and keywords matched inside string literals and comments, in rule 27 and in every
/// other rule that reads the query text) and #578 (rule 30 merging same-named tables from two
/// databases). The plans are built in code, as in the issues' repros.
/// </summary>
public class AnalyzerRuleGateTests
{
    private static ParsedPlan Analyze(PlanStatement stmt, AnalyzerConfig? config = null)
    {
        var plan = new ParsedPlan { Batches = [new PlanBatch { Statements = [stmt] }] };
        PlanAnalyzer.Analyze(plan, config);
        return plan;
    }

    private static AnalyzerConfig Disabled(params int[] rules) =>
        new() { Rules = new RulesConfig { Disabled = rules.ToList() } };

    private static bool Has(PlanStatement stmt, string warningType) =>
        stmt.PlanWarnings.Any(w => w.WarningType == warningType);

    private static bool Has(PlanNode node, string warningType) =>
        node.Warnings.Any(w => w.WarningType == warningType);

    // ---- #577: rule 5 and operators that never executed ------------------------------------

    private static PlanNode UnexecutedSort(long executions) => new()
    {
        PhysicalOp = "Sort",
        LogicalOp = "Sort",
        HasActualStats = true,
        ActualExecutions = executions,
        ActualRows = 0,
        EstimateRows = 1000
    };

    [Fact]
    public void Rule05_OperatorThatNeverExecuted_IsNotAnEstimateMismatch()
    {
        var node = UnexecutedSort(executions: 0);
        Analyze(new PlanStatement { RootNode = node });

        Assert.False(Has(node, "Row Estimate Mismatch"));
    }

    [Fact]
    public void Rule05_OperatorThatExecutedAndReturnedNothing_StillWarns()
    {
        var node = UnexecutedSort(executions: 1);
        Analyze(new PlanStatement { RootNode = node });

        Assert.True(Has(node, "Row Estimate Mismatch"));
    }

    // ---- #576: rule 6 is suppressed only by a rule 3 finding that exists --------------------

    private static (PlanStatement Stmt, PlanNode Node) UdfStatement(double cost)
    {
        var node = new PlanNode
        {
            PhysicalOp = "Compute Scalar",
            LogicalOp = "Compute Scalar",
            ScalarUdfs = [new ScalarUdfReference { FunctionName = "dbo.F" }]
        };
        var stmt = new PlanStatement
        {
            NonParallelPlanReason = "TSQLUserDefinedFunctionsNotParallelizable",
            StatementSubTreeCost = cost,
            RootNode = node
        };
        return (stmt, node);
    }

    [Fact]
    public void Rule06_Rule3Disabled_ScalarUdfWarningStays()
    {
        var (stmt, node) = UdfStatement(cost: 10);
        Analyze(stmt, Disabled(3));

        Assert.False(Has(stmt, "Serial Plan"));
        Assert.True(Has(node, "Scalar UDF"));
    }

    [Fact]
    public void Rule06_Rule3GatedOutByCost_ScalarUdfWarningStays()
    {
        // Rule 3 skips statements that cost under 1: they could never go parallel.
        var (stmt, node) = UdfStatement(cost: 0.5);
        Analyze(stmt);

        Assert.False(Has(stmt, "Serial Plan"));
        Assert.True(Has(node, "Scalar UDF"));
    }

    [Fact]
    public void Rule06_Rule3ExplainsTheUdf_OneFindingNotTwo()
    {
        var (stmt, node) = UdfStatement(cost: 10);
        Analyze(stmt);

        Assert.Single(stmt.PlanWarnings, w => w.WarningType == "Serial Plan");
        Assert.False(Has(node, "Scalar UDF"));
    }

    // ---- #579: text inside string literals and comments is not code -------------------------

    [Theory]
    [InlineData("SELECT 'OPTIMIZE FOR UNKNOWN'")]
    [InlineData("SELECT N'it''s OPTIMIZE FOR UNKNOWN'")]
    [InlineData("SELECT a FROM dbo.t -- OPTION (OPTIMIZE FOR UNKNOWN)")]
    [InlineData("SELECT a FROM dbo.t /* OPTION (OPTIMIZE FOR UNKNOWN) */")]
    [InlineData("SELECT a FROM dbo.t /* outer /* inner */ OPTIMIZE FOR UNKNOWN */")]
    public void Rule27_HintTextInLiteralOrComment_DoesNotWarn(string text)
    {
        var stmt = new PlanStatement { StatementText = text };
        Analyze(stmt);

        Assert.False(Has(stmt, "Optimize For Unknown"));
    }

    [Theory]
    [InlineData("SELECT a FROM dbo.t WHERE b = @b OPTION (OPTIMIZE FOR UNKNOWN)")]
    [InlineData("SELECT '--' AS x FROM dbo.t OPTION (OPTIMIZE FOR UNKNOWN)")]
    [InlineData("SELECT [it's] FROM dbo.t OPTION (OPTIMIZE FOR UNKNOWN)")]
    [InlineData("SELECT a FROM dbo.t /* note */ OPTION (OPTIMIZE FOR UNKNOWN)")]
    public void Rule27_RealHint_StillWarns(string text)
    {
        // A dash pair inside a string, a quote inside a bracketed name, and a closed comment
        // before the hint must not hide the hint that follows them.
        var stmt = new PlanStatement { StatementText = text };
        Analyze(stmt);

        Assert.True(Has(stmt, "Optimize For Unknown"));
    }

    [Theory]
    [InlineData("SELECT a FROM dbo.t -- OPTION (MAXDOP 1)", false)]
    [InlineData("SELECT a FROM dbo.t OPTION (MAXDOP 1)", true)]
    public void Rule03_Maxdop1InACommentIsNotAQueryHint(string text, bool expectWarning)
    {
        // Without MAXDOP 1 in the query itself, the setting came from the server, the database or
        // Resource Governor, and rule 3 stays quiet (untruncated text).
        var stmt = new PlanStatement
        {
            NonParallelPlanReason = "MaxDOPSetToOne",
            StatementSubTreeCost = 10,
            StatementText = text
        };
        Analyze(stmt);

        Assert.Equal(expectWarning, Has(stmt, "Serial Plan"));
    }

    [Theory]
    [InlineData("SELECT a FROM dbo.t WHERE b = @b /* OPTION (RECOMPILE) */", true)]
    [InlineData("SELECT a FROM dbo.t WHERE b = @b OPTION (RECOMPILE)", false)]
    public void Rule20_RecompileInACommentDoesNotSilenceTheWarning(string text, bool expectWarning)
    {
        var stmt = new PlanStatement
        {
            StatementSubTreeCost = 10,
            StatementText = text,
            Parameters = [new PlanParameter { Name = "@b" }]
        };
        Analyze(stmt);

        Assert.Equal(expectWarning, Has(stmt, "Local Variables"));
    }

    [Theory]
    [InlineData("-- DECLARE c CURSOR FOR SELECT a FROM dbo.t\nSELECT a FROM dbo.t", false)]
    [InlineData("DECLARE c CURSOR /* LOCAL */ FOR SELECT a FROM dbo.t", true)]
    [InlineData("DECLARE c CURSOR LOCAL FOR SELECT a FROM dbo.t", false)]
    public void Rule37_CursorDeclarationReadsOnlyCode(string text, bool expectWarning)
    {
        var stmt = new PlanStatement { StatementText = text };
        Analyze(stmt);

        Assert.Equal(expectWarning, Has(stmt, "Cursor Missing LOCAL"));
    }

    [Theory]
    [InlineData("SELECT a FROM dbo.t WHERE b NOT IN (SELECT c FROM dbo.u)", true)]
    [InlineData("SELECT a FROM dbo.t WHERE NOT EXISTS (SELECT 1 FROM dbo.u) -- was NOT IN", false)]
    public void Rule28_NotInMustBeInTheCode(string text, bool expectWarning)
    {
        var spool = new PlanNode
        {
            PhysicalOp = "Row Count Spool",
            LogicalOp = "Lazy Spool",
            EstimateRewinds = 20000
        };
        var antiSemiJoin = new PlanNode
        {
            PhysicalOp = "Nested Loops",
            LogicalOp = "Left Anti Semi Join",
            Predicate = "[dbo].[u].[c] IS NULL",
            Children = { spool }
        };
        spool.Parent = antiSemiJoin;
        Analyze(new PlanStatement { StatementText = text, RootNode = antiSemiJoin });

        Assert.Equal(expectWarning, Has(spool, "NOT IN with Nullable Column"));
    }

    [Fact]
    public void Rule26_RowGoalCauseIgnoresKeywordsInComments()
    {
        var scan = new PlanNode
        {
            PhysicalOp = "Index Scan",
            LogicalOp = "Index Scan",
            EstimateRows = 1,
            EstimateRowsWithoutRowGoal = 1000
        };
        Analyze(new PlanStatement
        {
            StatementText = "SELECT a FROM dbo.t WHERE EXISTS (SELECT 1 FROM dbo.u) -- was TOP (1)",
            RootNode = scan
        });

        var warning = Assert.Single(scan.Warnings, w => w.WarningType == "Row Goal");
        Assert.Contains("due to EXISTS.", warning.Message);
    }

    // ---- #578: rule 30 keys a table by database, schema and name -----------------------------

    private static MissingIndex Suggestion(string database) =>
        new() { Database = database, Schema = "dbo", Table = "T", Impact = 90 };

    [Fact]
    public void Rule30_SameNamedTablesInTwoDatabases_AreNotDuplicates()
    {
        var stmt = new PlanStatement { MissingIndexes = [Suggestion("A"), Suggestion("B")] };
        Analyze(stmt);

        Assert.False(Has(stmt, "Duplicate Index Suggestions"));
    }

    [Fact]
    public void Rule30_TwoSuggestionsForOneTable_AreStillDuplicates()
    {
        var stmt = new PlanStatement { MissingIndexes = [Suggestion("A"), Suggestion("A")] };
        Analyze(stmt);

        Assert.Single(stmt.PlanWarnings, w => w.WarningType == "Duplicate Index Suggestions");
    }
}
