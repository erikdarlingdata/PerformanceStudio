using System.Collections.Generic;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public class DdlScripterTests
{
    private static IndexInfo Index(
        string name,
        string type,
        string keyColumns,
        bool isPrimaryKey = false,
        bool isUnique = false,
        string includeColumns = "",
        string? filter = null,
        string compression = "NONE",
        int fillFactor = 0,
        string? partitionScheme = null,
        string? partitionColumn = null) => new()
        {
            IndexName = name,
            IndexType = type,
            IsUnique = isUnique,
            IsPrimaryKey = isPrimaryKey,
            KeyColumns = keyColumns,
            IncludeColumns = includeColumns,
            FilterDefinition = filter,
            RowCount = 1000,
            SizeMB = 1.5,
            DataCompression = compression,
            FillFactor = fillFactor,
            PartitionScheme = partitionScheme,
            PartitionColumn = partitionColumn
        };

    private static ColumnInfo Column(
        string name,
        string dataType,
        bool nullable = false,
        bool identity = false,
        bool computed = false,
        string? defaultValue = null,
        string? computedDef = null) => new()
        {
            OrdinalPosition = 1,
            ColumnName = name,
            DataType = dataType,
            IsNullable = nullable,
            IsIdentity = identity,
            IsComputed = computed,
            DefaultValue = defaultValue,
            ComputedDefinition = computedDef,
            IdentitySeed = 1,
            IdentityIncrement = 1
        };

    [Fact]
    public void FormatIndexes_NoIndexes_ReturnsComment()
    {
        var sql = DdlScripter.FormatIndexes("dbo.T", new List<IndexInfo>());
        Assert.Equal("-- No indexes found on dbo.T", sql);
    }

    [Fact]
    public void FormatIndexes_ClusteredPrimaryKey_EmitsAlterTableConstraint()
    {
        var sql = DdlScripter.FormatIndexes("dbo.T", new[] { Index("PK_T", "CLUSTERED", "Id", isPrimaryKey: true) });

        Assert.Contains("ALTER TABLE dbo.T", sql);
        Assert.Contains("ADD CONSTRAINT [PK_T]", sql);
        Assert.Contains("PRIMARY KEY CLUSTERED (Id)", sql);
    }

    [Fact]
    public void FormatIndexes_NonClustered_EmitsIncludeFilterAndOptions()
    {
        var ix = Index("IX_T", "NONCLUSTERED", "A, B", includeColumns: "C, D", filter: "[A] > 0", fillFactor: 90);
        var sql = DdlScripter.FormatIndexes("dbo.T", new[] { ix });

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_T]", sql);
        Assert.Contains("INCLUDE (C, D)", sql);
        Assert.Contains("WHERE [A] > 0", sql);
        Assert.Contains("WITH (FILLFACTOR = 90)", sql);
    }

    [Fact]
    public void FormatIndexes_Columnstore_EmitsColumnstoreCreate()
    {
        var sql = DdlScripter.FormatIndexes("dbo.T", new[] { Index("CCI_T", "CLUSTERED COLUMNSTORE", "") });
        Assert.Contains("CREATE CLUSTERED COLUMNSTORE INDEX [CCI_T]", sql);
    }

    [Fact]
    public void FormatIndexes_AlreadyBracketedName_IsNotDoubleBracketed()
    {
        // Canonical behavior: BracketName leaves an already-bracketed identifier alone.
        var sql = DdlScripter.FormatIndexes("dbo.T", new[] { Index("[IX_T]", "NONCLUSTERED", "A") });

        Assert.Contains("[IX_T]", sql);
        Assert.DoesNotContain("[[IX_T]]", sql);
    }

    [Fact]
    public void FormatColumns_NoColumns_ReturnsComment()
    {
        var sql = DdlScripter.FormatColumns("dbo.T", new List<ColumnInfo>(), new List<IndexInfo>());
        Assert.Equal("-- No columns found for dbo.T", sql);
    }

    [Fact]
    public void FormatColumns_TableWithColumnsAndPrimaryKey()
    {
        var cols = new[]
        {
            Column("Id", "int", identity: true),
            Column("Name", "nvarchar(50)", nullable: true)
        };
        var pk = Index("PK_T", "CLUSTERED", "Id", isPrimaryKey: true);

        var sql = DdlScripter.FormatColumns("dbo.T", cols, new[] { pk });

        Assert.Contains("CREATE TABLE dbo.T", sql);
        Assert.Contains("[Id] int IDENTITY(1, 1) NOT NULL", sql);
        Assert.Contains("[Name] nvarchar(50) NULL", sql);
        Assert.Contains("CONSTRAINT [PK_T]", sql);
        Assert.Contains("PRIMARY KEY CLUSTERED (Id)", sql);
    }

    [Fact]
    public void FormatColumns_ComputedColumn_EmitsAsExpression()
    {
        var cols = new[] { Column("Total", "int", computed: true, computedDef: "([A] + [B])") };
        var sql = DdlScripter.FormatColumns("dbo.T", cols, new List<IndexInfo>());

        Assert.Contains("[Total] AS ([A] + [B])", sql);
    }
}
