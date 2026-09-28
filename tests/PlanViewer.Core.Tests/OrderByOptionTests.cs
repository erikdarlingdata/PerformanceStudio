using PlanViewer.Cli.Commands;

namespace PlanViewer.Core.Tests;

/// <summary>
/// "query-store --order-by" with a value the query does not know used to fall back to CPU order
/// without a word, and the summary still said "top by" the misspelling. The value is now checked
/// while the command line is parsed. Any letter case still works, because the query lowercases it.
/// </summary>
public class OrderByOptionTests
{
    private static string[] Args(params string[] extra) =>
        new[] { "--server", "x", "--database", "x" }.Concat(extra).ToArray();

    [Fact]
    public void AnUnknownValue_IsAParseError_NamingTheAllowedValues()
    {
        var errors = QueryStoreCommand.Create().Parse(Args("--order-by", "bogus")).Errors
            .Select(error => error.Message)
            .ToList();

        Assert.NotEmpty(errors);
        var message = string.Join("\n", errors);
        Assert.Contains("bogus", message);
        foreach (var allowed in QueryStoreCommand.OrderByValues)
            Assert.Contains(allowed, message);
    }

    [Fact]
    public void EveryListedValue_IsAccepted_InAnyLetterCase()
    {
        foreach (var value in QueryStoreCommand.OrderByValues)
        {
            var spellings = new[] { value, value.ToUpperInvariant(), char.ToUpperInvariant(value[0]) + value[1..] };
            foreach (var spelled in spellings)
            {
                var parse = QueryStoreCommand.Create().Parse(Args("--order-by", spelled));

                Assert.True(parse.Errors.Count == 0,
                    $"--order-by {spelled}: {string.Join("; ", parse.Errors.Select(error => error.Message))}");
            }
        }
    }

    [Fact]
    public void WithoutTheOption_TheDefaultIsStillCpu()
    {
        var parse = QueryStoreCommand.Create().Parse(Args());

        Assert.Empty(parse.Errors);
        Assert.Equal("cpu", parse.GetValue<string>("--order-by"));
    }
}
