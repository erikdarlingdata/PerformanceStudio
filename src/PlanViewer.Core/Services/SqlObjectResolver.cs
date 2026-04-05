using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PlanViewer.Core.Services;

/// <summary>
/// The kind of SQL object found at a cursor position.
/// </summary>
public enum SqlObjectKind
{
    Table,
    View,
    Function,
    Procedure,
    Unknown
}

/// <summary>
/// Represents a resolved SQL object at a specific cursor position.
/// </summary>
public sealed class ResolvedSqlObject
{
    public required string SchemaName { get; init; }
    public required string ObjectName { get; init; }
    public required SqlObjectKind Kind { get; init; }

    /// <summary>
    /// Fully qualified [schema].[object] name.
    /// </summary>
    public string FullName => string.IsNullOrEmpty(SchemaName)
        ? ObjectName
        : $"{SchemaName}.{ObjectName}";
}

/// <summary>
/// Parses T-SQL text using ScriptDom and resolves what object is at a given cursor offset.
/// </summary>
public static class SqlObjectResolver
{
    /// <summary>
    /// Resolve the SQL object at the given zero-based character offset in the SQL text.
    /// Returns null if no recognizable object is at that position.
    /// </summary>
    public static ResolvedSqlObject? Resolve(string sqlText, int offset)
    {
        if (string.IsNullOrWhiteSpace(sqlText) || offset < 0 || offset >= sqlText.Length)
            return null;

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);

        using var reader = new StringReader(sqlText);
        var fragment = parser.Parse(reader, out var errors);

        if (fragment == null)
            return null;

        var visitor = new ObjectAtOffsetVisitor(offset);
        fragment.Accept(visitor);

        return visitor.Result;
    }

    /// <summary>
    /// Resolve all distinct SQL objects referenced in the text.
    /// </summary>
    public static IReadOnlyList<ResolvedSqlObject> ResolveAll(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText))
            return Array.Empty<ResolvedSqlObject>();

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);

        using var reader = new StringReader(sqlText);
        var fragment = parser.Parse(reader, out var errors);

        if (fragment == null)
            return Array.Empty<ResolvedSqlObject>();

        var visitor = new AllObjectsVisitor();
        fragment.Accept(visitor);

        return visitor.Results;
    }

    /// <summary>
    /// Walks the AST to find the object reference whose token span covers the given offset.
    /// </summary>
    private sealed class ObjectAtOffsetVisitor : TSqlFragmentVisitor
    {
        private readonly int _offset;

        public ResolvedSqlObject? Result { get; private set; }

        public ObjectAtOffsetVisitor(int offset) => _offset = offset;

        public override void Visit(NamedTableReference node)
        {
            if (Covers(node.SchemaObject, _offset))
            {
                Result = FromSchemaObjectName(node.SchemaObject, SqlObjectKind.Table);
            }
        }

        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget target)
            {
                if (CoversIdentifier(target.MultiPartIdentifier, _offset) ||
                    CoversToken(node.FunctionName, _offset))
                {
                    Result = FromFunctionCall(target.MultiPartIdentifier, node.FunctionName.Value, SqlObjectKind.Function);
                }
            }
            else if (CoversToken(node.FunctionName, _offset))
            {
                // Standalone function name — could be a scalar UDF or built-in
                Result = new ResolvedSqlObject
                {
                    SchemaName = "",
                    ObjectName = node.FunctionName.Value,
                    Kind = SqlObjectKind.Function
                };
            }
        }

        public override void Visit(SchemaObjectFunctionTableReference node)
        {
            if (Covers(node.SchemaObject, _offset))
            {
                Result = FromSchemaObjectName(node.SchemaObject, SqlObjectKind.Function);
            }
        }

        public override void Visit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference != null &&
                Covers(node.ProcedureReference.ProcedureReference.Name, _offset))
            {
                Result = FromSchemaObjectName(
                    node.ProcedureReference.ProcedureReference.Name,
                    SqlObjectKind.Procedure);
            }
        }
    }

    /// <summary>
    /// Collects all distinct object references in the SQL text.
    /// </summary>
    private sealed class AllObjectsVisitor : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ResolvedSqlObject> _results = new();

        public IReadOnlyList<ResolvedSqlObject> Results => _results;

        public override void Visit(NamedTableReference node)
        {
            Add(FromSchemaObjectName(node.SchemaObject, SqlObjectKind.Table));
        }

        public override void Visit(SchemaObjectFunctionTableReference node)
        {
            Add(FromSchemaObjectName(node.SchemaObject, SqlObjectKind.Function));
        }

        public override void Visit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference?.Name != null)
            {
                Add(FromSchemaObjectName(
                    node.ProcedureReference.ProcedureReference.Name,
                    SqlObjectKind.Procedure));
            }
        }

        private void Add(ResolvedSqlObject obj)
        {
            if (_seen.Add(obj.FullName))
                _results.Add(obj);
        }
    }

    // --- Helpers ---

    private static ResolvedSqlObject FromSchemaObjectName(SchemaObjectName name, SqlObjectKind kind)
    {
        return new ResolvedSqlObject
        {
            SchemaName = name.SchemaIdentifier?.Value ?? "",
            ObjectName = name.BaseIdentifier?.Value ?? "",
            Kind = kind
        };
    }

    private static ResolvedSqlObject FromFunctionCall(
        MultiPartIdentifier identifier, string functionName, SqlObjectKind kind)
    {
        // e.g., dbo.MyFunction() → schema = "dbo", object = "MyFunction"
        var parts = identifier.Identifiers;
        var schema = parts.Count > 0 ? parts[parts.Count - 1].Value : "";

        return new ResolvedSqlObject
        {
            SchemaName = schema,
            ObjectName = functionName,
            Kind = kind
        };
    }

    /// <summary>
    /// Check if a SchemaObjectName's token range covers the given offset.
    /// </summary>
    private static bool Covers(SchemaObjectName name, int offset)
    {
        if (name == null) return false;

        int start = name.StartOffset;
        int end = start + name.FragmentLength;
        return offset >= start && offset < end;
    }

    /// <summary>
    /// Check if a MultiPartIdentifier's token range covers the given offset.
    /// </summary>
    private static bool CoversIdentifier(MultiPartIdentifier identifier, int offset)
    {
        if (identifier == null) return false;

        int start = identifier.StartOffset;
        int end = start + identifier.FragmentLength;
        return offset >= start && offset < end;
    }

    /// <summary>
    /// Check if a single Identifier token covers the given offset.
    /// </summary>
    private static bool CoversToken(Identifier token, int offset)
    {
        if (token == null) return false;

        int start = token.StartOffset;
        int end = start + token.FragmentLength;
        return offset >= start && offset < end;
    }
}
