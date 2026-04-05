using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public class SqlObjectResolverTests
{
    [Fact]
    public void Resolve_TableInSelect_ReturnsTable()
    {
        var sql = "SELECT * FROM dbo.Posts";
        //                       ^--- offset 14 = start of "dbo.Posts"
        var offset = sql.IndexOf("dbo.Posts");

        var result = SqlObjectResolver.Resolve(sql, offset);

        Assert.NotNull(result);
        Assert.Equal("dbo", result.SchemaName);
        Assert.Equal("Posts", result.ObjectName);
        Assert.Equal(SqlObjectKind.Table, result.Kind);
    }

    [Fact]
    public void Resolve_TableInJoin_ReturnsTable()
    {
        var sql = "SELECT p.Id FROM dbo.Posts AS p JOIN dbo.Users AS u ON p.OwnerUserId = u.Id";
        var offset = sql.IndexOf("dbo.Users");

        var result = SqlObjectResolver.Resolve(sql, offset);

        Assert.NotNull(result);
        Assert.Equal("Users", result.ObjectName);
        Assert.Equal(SqlObjectKind.Table, result.Kind);
    }

    [Fact]
    public void Resolve_ClickOnObjectName_ReturnsTable()
    {
        var sql = "SELECT * FROM dbo.Posts";
        // Click on "Posts" part specifically
        var offset = sql.IndexOf("Posts");

        var result = SqlObjectResolver.Resolve(sql, offset);

        Assert.NotNull(result);
        Assert.Equal("Posts", result.ObjectName);
        Assert.Equal(SqlObjectKind.Table, result.Kind);
    }

    [Fact]
    public void Resolve_UnqualifiedTable_ReturnsEmptySchema()
    {
        var sql = "SELECT * FROM Posts";
        var offset = sql.IndexOf("Posts");

        var result = SqlObjectResolver.Resolve(sql, offset);

        Assert.NotNull(result);
        Assert.Equal("", result.SchemaName);
        Assert.Equal("Posts", result.ObjectName);
    }

    [Fact]
    public void Resolve_ClickOnKeyword_ReturnsNull()
    {
        var sql = "SELECT * FROM dbo.Posts";
        var offset = sql.IndexOf("SELECT");

        var result = SqlObjectResolver.Resolve(sql, offset);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_Procedure_ReturnsProcedure()
    {
        var sql = "EXEC dbo.sp_MyProc @param = 1";
        var offset = sql.IndexOf("dbo.sp_MyProc");

        var result = SqlObjectResolver.Resolve(sql, offset);

        Assert.NotNull(result);
        Assert.Equal("sp_MyProc", result.ObjectName);
        Assert.Equal(SqlObjectKind.Procedure, result.Kind);
    }

    [Fact]
    public void Resolve_TableValuedFunction_ReturnsFunction()
    {
        var sql = "SELECT * FROM dbo.MyFunction(1, 2)";
        var offset = sql.IndexOf("dbo.MyFunction");

        var result = SqlObjectResolver.Resolve(sql, offset);

        Assert.NotNull(result);
        Assert.Equal("MyFunction", result.ObjectName);
        Assert.Equal(SqlObjectKind.Function, result.Kind);
    }

    [Fact]
    public void ResolveAll_MultipleObjects_ReturnsAll()
    {
        var sql = @"
SELECT p.Id, u.DisplayName
FROM dbo.Posts AS p
JOIN dbo.Users AS u ON p.OwnerUserId = u.Id
WHERE p.PostTypeId = 1";

        var results = SqlObjectResolver.ResolveAll(sql);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.ObjectName == "Posts");
        Assert.Contains(results, r => r.ObjectName == "Users");
    }

    [Fact]
    public void ResolveAll_DuplicateTable_ReturnsDistinct()
    {
        var sql = @"
SELECT * FROM dbo.Posts
UNION ALL
SELECT * FROM dbo.Posts";

        var results = SqlObjectResolver.ResolveAll(sql);

        Assert.Single(results);
        Assert.Equal("Posts", results[0].ObjectName);
    }

    [Fact]
    public void Resolve_EmptyText_ReturnsNull()
    {
        Assert.Null(SqlObjectResolver.Resolve("", 0));
        Assert.Null(SqlObjectResolver.Resolve("   ", 0));
    }

    [Fact]
    public void Resolve_InvalidOffset_ReturnsNull()
    {
        var sql = "SELECT * FROM dbo.Posts";
        Assert.Null(SqlObjectResolver.Resolve(sql, -1));
        Assert.Null(SqlObjectResolver.Resolve(sql, sql.Length));
    }
}
