using System.Text;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public sealed class PlanXmlPreflightTests
{
    [Fact]
    public async Task ValidateAsync_RejectsStatementBudgetBeforeMaterialization()
    {
        var path = Path.Combine(Path.GetTempPath(), $"preflight-statements-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new StringBuilder(
                "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements>");
            for (var id = 0; id <= PlanOperations.DefaultMaxStatements; id++)
                xml.Append($"<StmtSimple StatementId=\"{id}\" StatementText=\"SELECT 1\" />");
            xml.Append("</Statements></Batch></BatchSequence></ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => PlanXmlPreflight.ValidateAsync(stream, TestContext.Current.CancellationToken));

            Assert.Contains("statement complexity", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, stream.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsFallbackStatementsBeforeMaterialization()
    {
        var path = Path.Combine(Path.GetTempPath(), $"preflight-fallback-statements-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new StringBuilder(
                "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><QueryPlan>");
            for (var id = 0; id <= PlanOperations.DefaultMaxStatements; id++)
                xml.Append($"<StmtSimple StatementId=\"{id}\" StatementText=\"SELECT 1\" />");
            xml.Append("</QueryPlan></ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            await using var stream = OpenPlan(path);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => PlanXmlPreflight.ValidateAsync(stream, TestContext.Current.CancellationToken));

            Assert.Contains("statement complexity", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, stream.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsAlternateStatementTypesBeforeMaterialization()
    {
        var path = Path.Combine(Path.GetTempPath(), $"preflight-alternate-statements-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new StringBuilder(
                "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements>");
            for (var id = 0; id <= PlanOperations.DefaultMaxStatements; id++)
                xml.Append($"<StmtUseDb Database=\"db{id}\" StatementType=\"USE DATABASE\" />");
            xml.Append("</Statements></Batch></BatchSequence></ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            await using var stream = OpenPlan(path);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => PlanXmlPreflight.ValidateAsync(stream, TestContext.Current.CancellationToken));

            Assert.Contains("statement complexity", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, stream.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsCursorOperationsAsStatementsBeforeMaterialization()
    {
        var path = Path.Combine(Path.GetTempPath(), $"preflight-cursor-statements-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new StringBuilder(
                "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements><StmtCursor><CursorPlan>");
            for (var id = 0; id < PlanOperations.DefaultMaxStatements; id++)
                xml.Append($"<Operation OperationType=\"Fetch\"><QueryPlan><RelOp NodeId=\"{id}\" /></QueryPlan></Operation>");
            xml.Append("</CursorPlan></StmtCursor></Statements></Batch></BatchSequence></ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            await using var stream = OpenPlan(path);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => PlanXmlPreflight.ValidateAsync(stream, TestContext.Current.CancellationToken));

            Assert.Contains("statement complexity", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, stream.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsExcessiveElementCountBeforeMaterialization()
    {
        const int maximumElements = PlanXmlPreflight.DefaultMaxXmlElements;
        var path = Path.Combine(Path.GetTempPath(), $"preflight-elements-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new StringBuilder("<ShowPlanXML>");
            for (var index = 0; index < maximumElements; index++)
                xml.Append("<Element />");
            xml.Append("</ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            await using var stream = OpenPlan(path);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => PlanXmlPreflight.ValidateAsync(stream, TestContext.Current.CancellationToken));

            Assert.Contains("element", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, stream.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsExcessiveAttributeCountBeforeMaterialization()
    {
        const int elementCount = (PlanXmlPreflight.DefaultMaxXmlAttributes / 5) + 1;
        var path = Path.Combine(Path.GetTempPath(), $"preflight-attributes-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new StringBuilder("<ShowPlanXML>");
            for (var index = 0; index < elementCount; index++)
                xml.Append("<Element A=\"1\" B=\"2\" C=\"3\" D=\"4\" E=\"5\" />");
            xml.Append("</ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            await using var stream = OpenPlan(path);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => PlanXmlPreflight.ValidateAsync(stream, TestContext.Current.CancellationToken));

            Assert.Contains("attribute", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, stream.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsExcessiveXmlDepthAndRewindsTheStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"preflight-depth-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new StringBuilder("<ShowPlanXML>");
            for (var depth = 0; depth <= PlanXmlPreflight.DefaultMaxXmlDepth; depth++)
                xml.Append("<Element>");
            for (var depth = 0; depth <= PlanXmlPreflight.DefaultMaxXmlDepth; depth++)
                xml.Append("</Element>");
            xml.Append("</ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => PlanXmlPreflight.ValidateAsync(stream, TestContext.Current.CancellationToken));

            Assert.Contains("depth", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, stream.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FileStream OpenPlan(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}
