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
    public void BuildReproScript_UnsafeValue_ExplainsThePlaceholder()
    {
        // A bare ? with no explanation would read as a tool bug rather than a
        // deliberate refusal to emit the plan's value.
        var plan = PlanWithParameter("@id", "int", "1; DROP TABLE dbo.Orders --");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.Contains("not a simple literal", sql);
        Assert.Contains("@id", sql);
    }

    [Fact]
    public void BuildReproScript_DroppedParameter_IsReportedInWarnings()
    {
        var plan = PlanWithParameter("@id", "int; DROP TABLE dbo.Orders --", "(1)");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.Contains("1 parameter(s) omitted", sql);
    }

    [Theory]
    [InlineData("int", "(42)", "42")]                                  // parenthesized integer
    [InlineData("int", "(-7)", "-7")]                                  // negative
    [InlineData("bit", "(0)", "0")]                                    // bit
    [InlineData("decimal(18,2)", "(1.50)", "1.50")]                    // decimal
    [InlineData("float", "(1.0000000000000000e+000)", "1.0000000000000000e+000")] // scientific
    [InlineData("money", "($10.50)", "$10.50")]                        // money
    [InlineData("varbinary(8)", "(0x1234ABCD)", "0x1234ABCD")]         // binary
    [InlineData("datetime", "('2024-01-01 00:00:00.000')", "'2024-01-01 00:00:00.000'")] // date literal
    [InlineData("nvarchar(50)", "N'O''Brien'", "N'O''Brien'")]         // doubled quote inside
    [InlineData("int", "NULL", "NULL")]                                // null
    public void BuildReproScript_RealWorldCompiledValues_SurviveTheFilter(
        string dataType, string compiledValue, string expected)
    {
        // Guards against the filter being so strict it degrades ordinary repro scripts.
        var plan = PlanWithParameter("@p", dataType, compiledValue.Replace("\"", "&quot;"));
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.Contains($"@p = {expected}", sql);
        Assert.DoesNotContain("@p = ?", sql);
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
