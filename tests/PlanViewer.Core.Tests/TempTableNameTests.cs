using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// SQL Server stores a #temp table under its visible name padded with underscores to 116
/// characters, followed by a 12-character hex suffix, and plans show that internal name.
/// CleanTempTableName strips the padding and the suffix. A name with no padding before its hex
/// run is not that pattern, and it must come back unchanged. Before the guard ported from
/// PerformanceMonitor b31e5d18, an all-hex name such as "#deadbeef1", or a table variable's
/// internal name such as "#A1B2C3D4", came back as a bare "#".
/// </summary>
public class TempTableNameTests
{
    [Theory]
    [InlineData("#u", "000000012653")]
    [InlineData("#OldUsers", "00000000000C")]
    [InlineData("#deadbeef", "00000000000A")]
    public void InternalName_ReducesToTheVisibleName(string visibleName, string suffix)
    {
        var internalName = visibleName.PadRight(116, '_') + suffix;

        Assert.Equal(visibleName, ShowPlanParser.CleanTempTableName(internalName));
    }

    [Theory]
    [InlineData("#deadbeef1")]
    [InlineData("#A1B2C3D4")]
    [InlineData("#OldUsers")]
    [InlineData("#t")]
    [InlineData("##global")]
    [InlineData("Users")]
    public void NameWithoutPadding_IsUnchanged(string name)
    {
        Assert.Equal(name, ShowPlanParser.CleanTempTableName(name));
    }
}
