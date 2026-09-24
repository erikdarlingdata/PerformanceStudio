using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class PlanAnalyzer
{
    private static bool HasBatchModeNode(PlanNode node)
    {
        var mode = node.ActualExecutionMode ?? node.ExecutionMode;
        if (string.Equals(mode, "Batch", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var child in node.Children)
        {
            if (HasBatchModeNode(child))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when a node scans or modifies a table variable rather than a real table. The scan's
    /// Object element renders a table variable's name as "[@tv]", where a real table always has a
    /// schema: "[db].[dbo].[t]" — so the leading @ alone tells them apart.
    /// </summary>
    private static bool IsTableVariable(PlanNode node) =>
        !string.IsNullOrEmpty(node.ObjectName) && node.ObjectName.StartsWith("@");

    /* #440: collects the operators it found, because this walk already knows exactly which ones
       touched a table variable and used to throw that away. Two lists rather than one, since the
       two warnings this feeds are about different operators: every operator referencing a table
       variable, versus only the ones modifying it (which is what forces the plan serial). */
    private static void CheckForTableVariables(PlanNode node, bool isModification,
        ref bool hasTableVar, ref bool modifiesTableVar,
        List<int>? referencingNodeIds = null, List<int>? modifyingNodeIds = null)
    {
        if (IsTableVariable(node))
        {
            hasTableVar = true;
            referencingNodeIds?.Add(node.NodeId);
            // The modification target is typically an Insert/Update/Delete operator on a table variable
            if (isModification && (node.PhysicalOp.Contains("Insert", StringComparison.OrdinalIgnoreCase)
                || node.PhysicalOp.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || node.PhysicalOp.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || node.PhysicalOp.Contains("Merge", StringComparison.OrdinalIgnoreCase)))
            {
                modifiesTableVar = true;
                modifyingNodeIds?.Add(node.NodeId);
            }
        }
        foreach (var child in node.Children)
            CheckForTableVariables(child, isModification, ref hasTableVar, ref modifiesTableVar,
                referencingNodeIds, modifyingNodeIds);
    }

    /// <summary>
    /// Detects the NOT IN with nullable column pattern: statement has NOT IN,
    /// and a nearby Nested Loops Anti Semi Join has an IS NULL residual predicate.
    /// Checks ancestors and their children (siblings of ancestors) since the IS NULL
    /// predicate may be on a sibling Anti Semi Join rather than a direct parent.
    /// </summary>
    private static bool HasNotInPattern(PlanNode spoolNode, PlanStatement stmt)
    {
        // Check statement text for NOT IN
        if (string.IsNullOrEmpty(stmt.StatementText) ||
            !Regex.IsMatch(stmt.StatementText, @"\bNOT\s+IN\b", RegexOptions.IgnoreCase))
            return false;

        // Walk up the tree checking ancestors and their children
        var parent = spoolNode.Parent;
        while (parent != null)
        {
            if (IsAntiSemiJoinWithIsNull(parent))
                return true;

            // Check siblings: the IS NULL predicate may be on a sibling Anti Semi Join
            // (e.g. outer NL Anti Semi Join has two children: inner NL Anti Semi Join + Row Count Spool)
            foreach (var sibling in parent.Children)
            {
                if (sibling != spoolNode && IsAntiSemiJoinWithIsNull(sibling))
                    return true;
            }

            parent = parent.Parent;
        }

        return false;
    }

    private static bool IsAntiSemiJoinWithIsNull(PlanNode node) =>
        node.PhysicalOp == "Nested Loops" &&
        node.LogicalOp.Contains("Anti Semi", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(node.Predicate) &&
        node.Predicate.Contains("IS NULL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true for rowstore scan operators (Index Scan, Clustered Index Scan,
    /// Table Scan). Excludes columnstore scans, spools, and constant scans.
    /// </summary>
    private static bool IsRowstoreScan(PlanNode node)
    {
        return node.PhysicalOp.Contains("Scan", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Spool", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Constant", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Columnstore", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when the predicate contains ONLY PROBE() bitmap filter(s)
    /// with no real residual predicate. PROBE alone is a bitmap filter pushed
    /// down from a hash join — not interesting by itself. If a real predicate
    /// exists alongside PROBE (e.g. "[col]=(1) AND PROBE(...)"), returns false.
    /// </summary>
    private static bool IsProbeOnly(string predicate)
    {
        // Strip all PROBE(...) expressions — PROBE args can contain nested parens
        var stripped = Regex.Replace(predicate, @"PROBE\s*\([^()]*(?:\([^()]*\)[^()]*)*\)", "",
            RegexOptions.IgnoreCase).Trim();

        // Remove leftover AND/OR connectors and whitespace
        stripped = Regex.Replace(stripped, @"\b(AND|OR)\b", "", RegexOptions.IgnoreCase).Trim();

        // If nothing meaningful remains, it was PROBE-only
        return stripped.Length == 0;
    }

    /// <summary>
    /// Strips PROBE(...) bitmap filter expressions from a predicate for display,
    /// leaving only the real residual predicate columns.
    /// </summary>
    private static string StripProbeExpressions(string predicate)
    {
        var stripped = Regex.Replace(predicate, @"\s*AND\s+PROBE\s*\([^()]*(?:\([^()]*\)[^()]*)*\)", "",
            RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"PROBE\s*\([^()]*(?:\([^()]*\)[^()]*)*\)\s*AND\s+", "",
            RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"PROBE\s*\([^()]*(?:\([^()]*\)[^()]*)*\)", "",
            RegexOptions.IgnoreCase);
        return stripped.Trim();
    }

    /// <summary>
    /// Returns true for any scan operator including columnstore.
    /// Excludes spools and constant scans.
    /// </summary>
    private static bool IsScanOperator(PlanNode node)
    {
        return node.PhysicalOp.Contains("Scan", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Spool", StringComparison.OrdinalIgnoreCase) &&
               !node.PhysicalOp.Contains("Constant", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects non-SARGable patterns in scan predicates.
    /// Returns a description of the issue, or null if the predicate is fine.
    /// </summary>
    private static string? DetectNonSargablePredicate(PlanNode node)
    {
        if (string.IsNullOrEmpty(node.Predicate))
            return null;

        // Only check rowstore scan operators — columnstore is designed to be scanned
        if (!IsRowstoreScan(node))
            return null;

        return DetectNonSargablePattern(node.Predicate, IsTableVariable(node));
    }

    /// <summary>
    /// The pattern half of <see cref="DetectNonSargablePredicate"/>: which non-SARGable shape, if
    /// any, a predicate ScalarString has.
    ///
    /// <para>Internal so predicate shapes can be tested as raw strings. The shapes that matter
    /// (compound AND/OR predicates, date ranges, parenthesized groups, AND inside a literal or a
    /// bracketed name) outnumber any sensible set of plan fixtures, and every one of them is
    /// decided entirely in this method and the helpers it calls.</para>
    ///
    /// <para><paramref name="isTableVariableScan"/> is true only when the caller has confirmed the
    /// scan reads a table variable (#561). A real table always renders a column dotted, at minimum
    /// [table].[col], but a table variable with no alias renders one as a bare name with no dotted
    /// qualifier at all — see <see cref="ColumnReferenceRegex"/>. Defaults to false so every
    /// existing caller keeps today's behavior unchanged.</para>
    /// </summary>
    internal static string? DetectNonSargablePattern(string predicate, bool isTableVariableScan = false)
    {
        // CASE expression in predicate — check first because CASE bodies
        // often contain CONVERT_IMPLICIT that isn't the root cause
        if (CaseInPredicateRegex.IsMatch(predicate))
            return "CASE expression in predicate";

        // CONVERT_IMPLICIT — most common non-SARGable pattern, but only when it converts the
        // COLUMN. Converting the parameter up to the column's type costs nothing (#436).
        if (ConvertImplicitWrapsColumn(predicate, isTableVariableScan))
            return "Implicit conversion (CONVERT_IMPLICIT)";

        // ISNULL / COALESCE wrapping column — on the column side only. ISNULL(@p, 0) on the
        // parameter side is a runtime constant and seeks fine; flagging it contradicted this
        // warning's own "wrapping a column" message. col = ISNULL(@p, col) is still caught,
        // because the column sits inside the function, on its side of the comparison.
        foreach (Match isnullMatch in IsnullCoalesceRegex.Matches(predicate))
        {
            if (IsFunctionOnColumnSide(predicate, isnullMatch, isTableVariableScan))
                return "ISNULL/COALESCE wrapping column";
        }

        // Common function calls on columns — but only if the function wraps a column,
        // not a parameter/variable. Split on comparison operators to check which side
        // the function is on. Predicate format: [db].[schema].[table].[col]>func(...)
        // Every match, not just the first: a parameter-side CONVERT_IMPLICIT now falls through to
        // here, and it is skipped below. Taking only the first match would let a benign conversion
        // sitting to the left of a real function-on-column hide it (#436).
        foreach (Match funcMatch in FunctionInPredicateRegex.Matches(predicate))
        {
            var funcName = funcMatch.Groups[1].Value.ToUpperInvariant();
            if (funcName != "CONVERT_IMPLICIT" && IsFunctionOnColumnSide(predicate, funcMatch, isTableVariableScan))
                return $"Function call ({funcName}) on column";
        }

        // Leading wildcard LIKE
        if (LeadingWildcardLikeRegex.IsMatch(predicate))
            return "Leading wildcard LIKE pattern";

        return null;
    }

    /// <summary>
    /// Checks whether any CONVERT_IMPLICIT in a predicate converts a COLUMN, which is the only
    /// version of it that costs a seek.
    ///
    /// <para><b>Why this is not just "contains CONVERT_IMPLICIT" (#436).</b> Data type precedence
    /// decides which side SQL Server converts, and it converts the LOWER-precedence side. Comparing a
    /// numeric(18,0) column to an int parameter converts the parameter UP:
    /// <c>[db].[dbo].[t].[col]=CONVERT_IMPLICIT(numeric(18,0),[@0],0)</c>. The column is untouched and
    /// still seekable — SQL Server will seek straight through that predicate given an index, and it
    /// raises no PlanAffectingConvert warning of its own. The damaging shape is the mirror image,
    /// <c>CONVERT_IMPLICIT(nvarchar(40),[db].[dbo].[t].[col],0)=[@d]</c>, where the conversion wraps
    /// the column and every row has to be converted before it can be compared.</para>
    ///
    /// <para>So the question is not whether a conversion is present but what is inside it, which is
    /// why this reads the CONVERT_IMPLICIT argument list rather than splitting on the comparison
    /// operator the way <see cref="IsFunctionOnColumnSide"/> does. The first argument is the target
    /// type and carries no brackets; a column reference in the remainder is the conversion input.</para>
    ///
    /// <para>Internal so the column-vs-variable line can be tested against raw predicate strings in
    /// showplan shape. <paramref name="isTableVariableScan"/> covers the unaliased table-variable
    /// case — a bare name with no dotted qualifier, e.g. <c>CONVERT_IMPLICIT(nvarchar(20),[S],0)=[@n]</c>
    /// — which this method cannot tell from a parameter or an expression on its own; see
    /// <see cref="IsColumnReference"/> (#561).</para>
    /// </summary>
    internal static bool ConvertImplicitWrapsColumn(string predicate, bool isTableVariableScan = false)
    {
        foreach (Match match in ConvertImplicitRegex.Matches(predicate))
        {
            // The regex ends at the opening paren, so its last character is where the args start.
            var arguments = ExtractBalancedArguments(predicate, match.Index + match.Length - 1);

            // Unparseable means we cannot tell what is being converted. Assume the worst, matching
            // IsFunctionOnColumnSide, rather than silently dropping a real conversion.
            if (arguments == null || IsColumnReference(arguments, isTableVariableScan))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="text"/> names a column. A real table always renders a column
    /// dotted, at minimum <c>[table].[col]</c>, so <see cref="ColumnReferenceRegex"/> alone is
    /// enough there. An unaliased table-variable column has no dotted qualifier to match — it
    /// renders as a bare bracketed name identical in shape to a parameter or an expression column
    /// (#561). <paramref name="isTableVariableScan"/> is the caller's proof the scan is on a table
    /// variable, so a bare name here can safely be read as a column too, unless it is a parameter
    /// or variable (<c>[@p1]</c>), an optimizer expression (<c>[Expr1003]</c>), or the name of a
    /// function call (followed by <c>(</c>) rather than a reference. A string literal that looks
    /// bracketed (<c>'[Y]'</c>) is never read as a name at all — see <see cref="BracketedNameRegex"/>.
    /// </summary>
    private static bool IsColumnReference(string text, bool isTableVariableScan)
    {
        if (ColumnReferenceRegex.IsMatch(text))
            return true;

        if (!isTableVariableScan)
            return false;

        foreach (Match match in BracketedNameRegex.Matches(text))
        {
            if (!match.Groups["name"].Success || match.Groups["call"].Success)
                continue; // a string literal, or the name of a function

            var name = match.Groups["name"].Value;
            if (name.StartsWith("[@", StringComparison.Ordinal))
                continue; // a parameter or a variable: [@p1]

            if (ExpressionColumnRegex.IsMatch(name))
                continue; // an optimizer-generated expression, not an actual column: [Expr1003]

            return true; // a bare name — a column on this table-variable scan
        }

        return false;
    }

    /// <summary>
    /// Returns the text between the parenthesis at <paramref name="openParenIndex"/> and its match,
    /// or null if the parentheses do not balance. Needed because the target type of a conversion can
    /// carry its own parentheses — numeric(18,0), varchar(50) — so the first ')' is not the end.
    /// </summary>
    private static string? ExtractBalancedArguments(string text, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return text[(openParenIndex + 1)..i];
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether a function call in a predicate is on the column side of the comparison.
    /// Predicate ScalarStrings look like: [db].[schema].[table].[col]>dateadd(day,(0),[@var])
    /// If the function is only on the parameter/literal side, it's still SARGable.
    ///
    /// <para><b>Only the function's own comparison is read (#556).</b> A compound predicate is
    /// several comparisons joined by AND/OR, and the function belongs to exactly one of them.
    /// Splitting the whole predicate at its FIRST operator instead put every later comparison,
    /// column and all, on the function's side: in <c>[t].[A]=[@1] AND [t].[B]=CONVERT(tinyint,[@2],0)</c>
    /// the CONVERT looked like it shared a side with [t].[B], and so did the dateadd in the
    /// everyday range <c>[t].[d]&gt;=dateadd(day,(-7),getdate()) AND [t].[d]&lt;getdate()</c>.</para>
    ///
    /// <para><paramref name="isTableVariableScan"/> is passed straight to
    /// <see cref="IsColumnReference"/> to cover the unaliased table-variable case, e.g.
    /// <c>abs([X])=(1)</c> (#561).</para>
    /// </summary>
    private static bool IsFunctionOnColumnSide(string predicate, Match funcMatch, bool isTableVariableScan = false)
    {
        var comparison = ComparisonContaining(predicate, funcMatch.Index, out var offset);

        var compMatch = ComparisonOperatorRegex.Match(comparison);
        if (!compMatch.Success)
            return true; // No comparison found — can't determine side, assume worst case

        var compPos = compMatch.Index;
        var funcPos = funcMatch.Index - offset;

        // The side of this comparison the function is on, and whether a column shares it
        string side = funcPos < compPos
            ? comparison[..compPos]
            : comparison[(compPos + compMatch.Length)..];

        // Same column-vs-variable distinction ConvertImplicitWrapsColumn needs, so it shares the
        // one helper rather than keeping a second copy of the logic in sync by hand.
        return IsColumnReference(side, isTableVariableScan);
    }

    /// <summary>
    /// The single comparison around <paramref name="position"/>: the text between the nearest
    /// AND/OR before it and the nearest after it. <paramref name="offset"/> is where that text
    /// starts in <paramref name="predicate"/>, so positions can be translated into it.
    ///
    /// <para>Operators are split on at every depth, not just the top level: a parenthesized group
    /// like <c>[t].[A]=(1) AND ([t].[B]=f([@p]) OR [t].[C]=(3))</c> has to come apart into its
    /// three comparisons, or the group would be read as one. The leftover grouping parentheses
    /// cannot move a comparison operator or add a column, so they are harmless. No function in a
    /// ScalarString takes AND/OR inside its arguments; CASE does, and it is caught earlier.</para>
    /// </summary>
    private static string ComparisonContaining(string predicate, int position, out int offset)
    {
        var start = 0;
        var end = predicate.Length;

        foreach (Match match in LogicalOperatorRegex.Matches(predicate))
        {
            if (!match.Groups[1].Success)
                continue; // a string literal or bracketed name, skipped whole

            if (match.Index + match.Length <= position)
            {
                start = match.Index + match.Length;
            }
            else
            {
                end = match.Index;
                break;
            }
        }

        offset = start;
        return predicate[start..end];
    }

    /// <summary>
    /// Verifies the OR expansion chain walking up from a Concatenation node:
    /// Nested Loops → Merge Interval → TopN Sort → [Compute Scalar] → Concatenation
    /// </summary>
    private static bool IsOrExpansionChain(PlanNode concatenationNode)
    {
        // Walk up, skipping Compute Scalar
        var parent = concatenationNode.Parent;
        while (parent != null && parent.PhysicalOp == "Compute Scalar")
            parent = parent.Parent;

        // Expect TopN Sort (XML says "TopN Sort", parser normalizes to "Top N Sort")
        if (parent == null || parent.LogicalOp != "Top N Sort")
            return false;

        // Walk up to Merge Interval
        parent = parent.Parent;
        if (parent == null || parent.PhysicalOp != "Merge Interval")
            return false;

        // Walk up to Nested Loops
        parent = parent.Parent;
        if (parent == null || parent.PhysicalOp != "Nested Loops")
            return false;

        // If this Nested Loops is inside an Anti/Semi Join, this is a NOT IN/IN
        // subquery pattern (Merge Interval optimizing range lookups), not an OR expansion
        var nlParent = parent.Parent;
        if (nlParent != null && nlParent.LogicalOp != null &&
            nlParent.LogicalOp.Contains("Semi"))
            return false;

        return true;
    }

    /// <summary>
    /// True when a lookup branch under an OR expansion's Concatenation builds its seek value from
    /// another input. A join OR does: in ON u.Id = p.OwnerUserId OR u.Id = p.LastEditorUserId the
    /// branches produce [Posts].[OwnerUserId] and [Posts].[LastEditorUserId], once per outer row.
    /// The dynamic seek for an IN list of parameters (#558) has the same operator shape, but its
    /// branches produce only parameters and literals ([@p1], (62)), which no outer row changes.
    /// </summary>
    private static bool LookupReadsAnotherInput(PlanNode branch)
    {
        var values = branch.PhysicalOp == "Constant Scan"
            ? branch.ConstantScanValues
            : branch.DefinedValues;

        // Nothing to read, so nothing proves a parameter list: keep the warning.
        if (string.IsNullOrEmpty(values))
            return true;

        return ReadsAnotherInput(values);
    }

    /// <summary>
    /// True when a ScalarString names anything other than a parameter or a variable: a column
    /// ([db].[dbo].[T].[c], or @tv.[c] as [v].[c] on a table variable) or an expression column
    /// ([Expr1003]). Function names ([dbo].[fn](...)) and string literals are skipped. An
    /// expression column counts too: an OR join on o.X + 1 renders its branches as [Expr1002],
    /// computed on the outer input. The Constant Scan under a lookup branch is normally empty,
    /// so the branch has no expression of its own to name, and a name that cannot be proved to
    /// be a parameter keeps the warning, as the shape check alone did. Internal so the shapes
    /// can be tested as raw strings.
    /// </summary>
    internal static bool ReadsAnotherInput(string scalarString)
    {
        foreach (Match match in BracketedNameRegex.Matches(scalarString))
        {
            if (!match.Groups["name"].Success || match.Groups["call"].Success)
                continue; // a string literal, or the name of a function

            var name = match.Groups["name"].Value;
            if (name.StartsWith("[@", StringComparison.Ordinal) &&
                !name.Contains("].[", StringComparison.Ordinal))
                continue; // a parameter or a variable: [@p1]

            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds Sort and Hash Match operators in the tree that consume memory.
    /// </summary>
    /// <summary>
    /// Returns true if the plan contains an adaptive join that executed as a Nested Loop.
    /// Indicates a memory grant was sized for the hash alternative but never needed.
    /// </summary>
    private static bool HasAdaptiveJoinChoseNestedLoop(PlanNode node)
    {
        if (node.IsAdaptive && node.ActualJoinType != null
            && node.ActualJoinType.Contains("Nested", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var child in node.Children)
            if (HasAdaptiveJoinChoseNestedLoop(child))
                return true;

        return false;
    }
}
