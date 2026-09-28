using Microsoft.SqlServer.TransactSql.ScriptDom;
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
        Assert.Contains("1 parameter(s) omitted", sql);
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

    [Fact]
    public void BuildReproScript_AutoParameterizedNames_AreDeclaredAndAssigned()
    {
        /* #590: simple and forced parameterization name their parameters @0, @1, ... .
           These were dropped, so the script ran the statement without declaring them and
           failed with "Must declare the scalar variable". The ParameterList is copied from
           a forced-parameterization plan on SQL Server 2025, in its order. */
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements>
                <StmtSimple>
                  <QueryPlan>
                    <ParameterList>
                      <ColumnReference Column="@1" ParameterDataType="int" ParameterCompiledValue="(0)" />
                      <ColumnReference Column="@0" ParameterDataType="nvarchar(4000)" ParameterCompiledValue="N'b'" />
                    </ParameterList>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        var sql = ReproScriptBuilder.BuildReproScript(
            "(@0 nvarchar(4000),@1 int)select t . id from dbo . T as t where t . v = @0 and t . id > @1",
            "db", plan, null);

        Assert.Contains("N'@1 int, @0 nvarchar(4000)'", sql);
        Assert.Contains("@1 = 0", sql);
        Assert.Contains("@0 = N'b'", sql);
        Assert.DoesNotContain("omitted", sql);
        ParseScript(sql);
    }

    [Fact]
    public void BuildReproScript_ParameterUsedByTwoStatements_IsDeclaredOnce()
    {
        /* A batch's plan lists each statement's parameters, so a parameter that two statements
           use appears twice. Declaring it twice fails with "The variable name '@id' has already
           been declared". The ParameterLists are copied from such a plan on SQL Server 2025. */
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements>
                <StmtSimple><QueryPlan><ParameterList>
                  <ColumnReference Column="@id" ParameterDataType="int" ParameterCompiledValue="(1)" />
                </ParameterList></QueryPlan></StmtSimple>
                <StmtSimple><QueryPlan><ParameterList>
                  <ColumnReference Column="@id" ParameterDataType="int" ParameterCompiledValue="(1)" />
                </ParameterList></QueryPlan></StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        var sql = ReproScriptBuilder.BuildReproScript(
            "(@id int)SELECT COUNT_BIG(*) AS a FROM dbo.T AS t WHERE t.id = @id\n; SELECT COUNT_BIG(*) AS b FROM dbo.T AS t WHERE t.id > @id",
            "db", plan, null);

        Assert.Contains("N'@id int',", sql);
        Assert.Equal(1, sql.Split("@id = 1").Length - 1);
        ParseScript(sql);
    }

    [Fact]
    public void BuildReproScript_AutoParameterTypedDifferentlyByTwoStatements_IsLeftOut()
    {
        /* Each auto-parameterized statement numbers its own parameters and types them by its
           literal, so a batch's estimated plan can list @1 smallint and @1 tinyint. Its
           statement text is the literal text, which doesn't use @1, so the script runs the
           batch as it is. The ParameterLists are copied from such a plan on SQL Server 2025. */
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements>
                <StmtSimple><QueryPlan><ParameterList>
                  <ColumnReference Column="@1" ParameterDataType="smallint" ParameterCompiledValue="(22656)" />
                </ParameterList></QueryPlan></StmtSimple>
                <StmtSimple><QueryPlan><ParameterList>
                  <ColumnReference Column="@1" ParameterDataType="tinyint" ParameterCompiledValue="(11)" />
                </ParameterList></QueryPlan></StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        var sql = ReproScriptBuilder.BuildReproScript(
            "SELECT t.id FROM dbo.T AS t WHERE t.id = 22656\n; SELECT t.v FROM dbo.T AS t WHERE t.id = 11",
            "db", plan, null);

        Assert.DoesNotContain("EXECUTE sys.sp_executesql", sql);
        Assert.Contains("different data type in different statements (left out): @1.", sql);
        Assert.Contains("SELECT t.v FROM dbo.T AS t WHERE t.id = 11", sql);
        Assert.DoesNotContain("omitted", sql);
        ParseScript(sql);
    }

    [Fact]
    public void BuildReproScript_ParameterNameEndingInALineBreak_IsDropped()
    {
        /* The XML parser turns a line break in an attribute into a space unless it is written
           as a character reference. ^...$ let this name through, because $ also matches
           before a final line break. */
        var plan = PlanWithParameter("@id&#10;", "int", "(1)");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.Contains("1 parameter(s) omitted", sql);
    }

    [Fact]
    public void BuildReproScript_CompiledValueEndingInALineBreak_BecomesPlaceholder()
    {
        var plan = PlanWithParameter("@id", "int", "42&#10;");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.Contains("@id = ?", sql);
    }

    [Fact]
    public void BuildReproScript_CompiledValueInAnotherScriptsDigits_BecomesPlaceholder()
    {
        /* \d matches these Arabic-Indic digits, but T-SQL doesn't read them as a number. */
        var plan = PlanWithParameter("@id", "int", "٤٢");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.Contains("@id = ?", sql);
    }

    [Theory]
    [InlineData("tinyint")]
    [InlineData("decimal(18,2)")]
    [InlineData("nvarchar(max)")]
    [InlineData("nvarchar(4000)")]
    [InlineData("datetime2(7)")]
    [InlineData("datetimeoffset(3)")]
    [InlineData("time(0)")]
    [InlineData("sys.geography")]
    [InlineData("sys.hierarchyid")]
    [InlineData("sql_variant")]
    [InlineData("xml")]
    [InlineData("json")]
    [InlineData("vector(3)")]
    [InlineData("vector(3,float16)")]
    [InlineData("[dbo].[Amount]")]
    public void BuildReproScript_DataTypesFromRealPlans_AreKept(string dataType)
    {
        /* Every type here but the last is a ParameterDataType that SQL Server 2025 wrote into
           a plan. An alias type shows up as its base type, and a typed xml parameter as xml.
           No plan here used brackets, but the check before #590 allowed them, so this keeps
           them working. */
        var plan = PlanWithParameter("@p", dataType, "NULL");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT @p", "db", plan, null);

        Assert.Contains($"N'@p {dataType}'", sql);
        Assert.DoesNotContain("omitted", sql);
    }

    [Theory]
    [InlineData("int) SELECT 2 SELECT (1")] // text after the closing paren
    [InlineData("decimal(18,2")]            // no closing paren
    [InlineData("int&#10;")]                // ends in a line break
    [InlineData("a.b.c.d")]                 // four-part name
    [InlineData("vector(3,&#10;GO&#10;)")]  // GO on a line of its own
    [InlineData("nvarchar(٤)")]        // an Arabic-Indic digit
    [InlineData("nvarchar(４)")]        // a full-width digit
    [InlineData("varchar(max,2)")]          // max takes no second part
    public void BuildReproScript_MalformedDataType_IsDropped(string dataType)
    {
        /* The check before #590 was a list of characters, and it passed every one of these but
           the one with GO. */
        var plan = PlanWithParameter("@id", dataType, "(1)");
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", "db", plan, null);

        Assert.Contains("1 parameter(s) omitted", sql);
        Assert.DoesNotContain("SELECT 2", sql);
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

    // The header comment shows the plan's database name. A crafted name must stay inside it:
    // "*/" would close the comment, "/*" would open a nested one that swallows the script,
    // and a line break could put GO on a line of its own. ScriptDom parses each script, so a
    // statement the name smuggled out would show up as a PRINT or an extra batch.
    //
    // The USE line keeps the name as it is, line breaks included, on purpose. It doubles "]",
    // and go-sqlcmd and ODBC sqlcmd do not split a batch inside a bracketed name. A client that
    // splits at every GO line is out of scope: the statement text can hold such a line too.
    [Theory]
    [InlineData("master*/\nGO\nPRINT 'INJECTED';\nGO\n/*")]  // its own batch in a GO-aware client
    [InlineData("master*/ PRINT 'INJECTED'; /*")]            // same batch, no GO needed
    [InlineData("master\r\nGO\r\nPRINT 'INJECTED';\r\nGO")] // line breaks alone
    [InlineData("master/*")]                                  // nested comment
    [InlineData("master/*/")]                                 // delimiters that overlap
    [InlineData("master*/*")]
    [InlineData("master\vGO\fPRINT 'INJECTED';\u0085GO\u2028x\u2029y")] // VT, FF, NEL, LS, PS
    public void BuildReproScript_HostileDatabaseName_StaysInTheHeaderComment(string databaseName)
    {
        var sql = ReproScriptBuilder.BuildReproScript("SELECT 1", databaseName, null, null);

        var script = ParseScript(sql);
        Assert.Single(script.Batches);
        Assert.DoesNotContain(script.Batches[0].Statements, s => s is PrintStatement);

        var header = HeaderComment(sql);
        var databaseLine = Assert.Single(header.Split('\n'), line => line.StartsWith("Database: [", StringComparison.Ordinal));
        Assert.StartsWith("Database: [master", databaseLine);
        Assert.DoesNotMatch(@"[\p{Cc}\u2028\u2029]", databaseLine.TrimEnd('\r'));
        Assert.DoesNotContain(header.Split('\n'), line => line.Trim() == "GO");
    }

    [Fact]
    public void BuildReproScript_HostileSource_StaysInTheHeaderComment()
    {
        var sql = ReproScriptBuilder.BuildReproScript(
            "SELECT 1", "db", null, null, source: "x*/ PRINT 'INJECTED'; /*");

        var script = ParseScript(sql);
        Assert.DoesNotContain(script.Batches.SelectMany(b => b.Statements), s => s is PrintStatement);
        Assert.Contains("Source: x* / PRINT 'INJECTED'; / *", HeaderComment(sql));
    }

    private static TSqlScript ParseScript(string sql)
    {
        var fragment = new TSql160Parser(initialQuotedIdentifiers: true)
            .Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
        return (TSqlScript)fragment;
    }

    // Everything up to the first "*/", which must be the header's own closing line: a value
    // that ended the comment early would put it somewhere else.
    private static string HeaderComment(string sql)
    {
        Assert.StartsWith("/*", sql);
        var end = sql.IndexOf("*/", StringComparison.Ordinal);
        Assert.Equal('\n', sql[end - 1]);
        Assert.DoesNotContain("/*", sql[2..end]);
        return sql[..end];
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
