using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

public class NonClusteredIndexCountTests
{
    [Fact]
    public void Update_WithFiveNonClusteredIndexes_CountIsFive()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("multi_index_update_plan.sqlplan");
        var stmt = PlanTestHelper.FirstStatement(plan);
        var updateNode = PlanTestHelper.FindNode(stmt.RootNode!, 1)!;

        Assert.Contains("Update", updateNode.PhysicalOp, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, updateNode.NonClusteredIndexCount);
    }

    [Fact]
    public void Insert_WithFiveNonClusteredIndexes_CountIsFive()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("multi_index_insert_plan.sqlplan");
        var stmt = PlanTestHelper.FirstStatement(plan);
        var insertNode = PlanTestHelper.FindNode(stmt.RootNode!, 0)!;

        Assert.Contains("Insert", insertNode.PhysicalOp, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, insertNode.NonClusteredIndexCount);
    }

    [Fact]
    public void Delete_WithFiveNonClusteredIndexes_CountIsFive()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("multi_index_delete_plan.sqlplan");
        var stmt = PlanTestHelper.FirstStatement(plan);
        var deleteNode = PlanTestHelper.FindNode(stmt.RootNode!, 0)!;

        Assert.Contains("Delete", deleteNode.PhysicalOp, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, deleteNode.NonClusteredIndexCount);
    }

    [Fact]
    public void ReadOperator_HasZeroNonClusteredIndexCount()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("key_lookup_plan.sqlplan");
        var stmt = PlanTestHelper.FirstStatement(plan);

        void AssertZero(PlanNode node)
        {
            Assert.Equal(0, node.NonClusteredIndexCount);
            foreach (var child in node.Children)
                AssertZero(child);
        }
        AssertZero(stmt.RootNode!);
    }
}
