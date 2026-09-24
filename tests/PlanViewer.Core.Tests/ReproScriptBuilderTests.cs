using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The repro script declares the parameters itself, so the declaration list that a cached
/// sp_executesql statement starts with must not reach the script's query text. The list is found
/// by the parser that parameter substitution uses (<c>ParameterSubstitution.DeclarationListEnd</c>).
/// </summary>
public class ReproScriptBuilderTests
{
    private const string Plan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
          <BatchSequence><Batch><Statements>
            <StmtSimple>
              <QueryPlan>
                <ParameterList>
                  <ColumnReference Column="@id" ParameterDataType="decimal(18,2)" ParameterCompiledValue="(42.50)" />
                </ParameterList>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void BuildReproScript_DeclarationList_IsLeftOutOfTheQueryText()
    {
        var sql = ReproScriptBuilder.BuildReproScript(
            "(@id decimal(18,2))SELECT * FROM dbo.T WHERE Id = @id", "db", Plan, null);

        Assert.Contains("SELECT * FROM dbo.T WHERE Id = @id", sql);
        Assert.DoesNotContain("(@id decimal(18,2))SELECT", sql);
        Assert.Contains("@id = 42.50", sql);
    }

    [Fact]
    public void BuildReproScript_DeclarationListCutOffByTruncation_IsKeptAsItIs()
    {
        /* The parser reports a list that never closes as -1. The text stays as it was, as it did
           before the parser was shared, and the -1 must never reach a slice. */
        var sql = ReproScriptBuilder.BuildReproScript("(@id decimal(18,2),@x nvarch", "db", Plan, null);

        Assert.Contains("(@id decimal(18,2),@x nvarch", sql);
    }
}
