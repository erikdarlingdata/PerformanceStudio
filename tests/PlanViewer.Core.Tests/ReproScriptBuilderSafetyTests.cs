using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Plan XML is untrusted input — a .sqlplan can be crafted or emailed. These cover the
/// generated script staying inert: the repro script is executed by ActualPlanExecutor
/// and handed to users to run, so a hostile ParameterList must not reach it intact.
/// </summary>
public class ReproScriptBuilderSafetyTests
{
    private static string PlanWithParameter(string column, string dataType, string compiledValue) =>
        $"""
         <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
           <BatchSequence><Batch><Statements>
             <StmtSimple>
               <QueryPlan>
                 <ParameterList>
                   <ColumnReference Column="{column}" ParameterDataType="{dataType}" ParameterCompiledValue="{compiledValue}" />
                 </ParameterList>
               </QueryPlan>
             </StmtSimple>
           </Statements></Batch></BatchSequence>
         </ShowPlanXML>
         """;

    [Fact]
    public void BuildReproScript_LegitimateParameter_IsEmittedVerbatim()
    {
        var plan = PlanWithParameter("@id", "int", "(42)");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.Contains("EXECUTE sys.sp_executesql", sql);
        Assert.Contains("@id int", sql);
        Assert.Contains("@id = 42", sql);
    }

    [Fact]
    public void BuildReproScript_StringParameter_KeepsQuotedLiteral()
    {
        var plan = PlanWithParameter("@name", "nvarchar(50)", "N'Erik'");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.Contains("@name = N'Erik'", sql);
    }

    [Fact]
    public void BuildReproScript_CompiledValueBreakingOutOfLiteral_BecomesPlaceholder()
    {
        // The injection this guards: a value that closes its own literal and appends T-SQL.
        var plan = PlanWithParameter("@id", "int", "1; DROP TABLE dbo.Orders --");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.Contains("@id = ?", sql);
    }

    [Fact]
    public void BuildReproScript_CompiledValueWithUnbalancedQuote_BecomesPlaceholder()
    {
        var plan = PlanWithParameter("@name", "nvarchar(50)", "N'x'; EXEC sp_who --'");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.DoesNotContain("sp_who", sql);
        Assert.Contains("@name = ?", sql);
    }

    [Fact]
    public void BuildReproScript_HostileParameterName_IsDroppedEntirely()
    {
        var plan = PlanWithParameter("@id = 1, @x int = 1; SHUTDOWN --", "int", "(1)");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.DoesNotContain("SHUTDOWN", sql);
    }

    [Fact]
    public void BuildReproScript_HostileDataType_IsDroppedEntirely()
    {
        var plan = PlanWithParameter("@id", "int; DROP TABLE dbo.Orders --", "(1)");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.DoesNotContain("DROP TABLE", sql);
    }

    [Fact]
    public void ExtractParametersFromPlan_StillReturnsRawParameters()
    {
        // The filter lives in script generation, not extraction — callers that only
        // display parameters should still see what the plan actually contained.
        var plan = PlanWithParameter("@id", "int", "(42)");
        var parameters = ReproScriptBuilder.ExtractParametersFromPlan(plan);

        Assert.Single(parameters);
        Assert.Equal("@id", parameters[0].Name);
        Assert.Equal("42", parameters[0].CompiledValue);
    }
}
