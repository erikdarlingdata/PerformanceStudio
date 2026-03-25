using System.Collections.Generic;

namespace PlanViewer.Core.Models;

public class ServerMetadata
{
    // Instance
    public string? ServerName { get; set; }
    public string? ProductVersion { get; set; }
    public string? ProductLevel { get; set; }
    public string? Edition { get; set; }
    public bool IsAzure { get; set; }

    // Hardware
    public int CpuCount { get; set; }
    public long PhysicalMemoryMB { get; set; }

    // Instance settings
    public int MaxDop { get; set; }
    public int CostThresholdForParallelism { get; set; }
    public long MaxServerMemoryMB { get; set; }

    // Database-level (refreshed on DB context switch)
    public DatabaseMetadata? Database { get; set; }

    /// <summary>
    /// Whether sys.database_scoped_configurations is available (SQL 2016+ or Azure).
    /// </summary>
    public bool SupportsScopedConfigs =>
        IsAzure || (int.TryParse(ProductVersion?.Split('.')[0], out var major) && major >= 13);

    /// <summary>
    /// Whether sys.query_store_wait_stats is available (SQL 2017+ or Azure).
    /// </summary>
    public bool SupportsQueryStoreWaitStats =>
        IsAzure || (int.TryParse(ProductVersion?.Split('.')[0], out var major) && major >= 14);
}

public class DatabaseMetadata
{
    public string Name { get; set; } = "";
    public int CompatibilityLevel { get; set; }
    public string CollationName { get; set; } = "";

    // Isolation — notable if on
    public int SnapshotIsolationState { get; set; }
    public bool IsReadCommittedSnapshotOn { get; set; }

    // Stats — notable if off
    public bool IsAutoCreateStatsOn { get; set; }
    public bool IsAutoUpdateStatsOn { get; set; }

    // Stats async — notable if on
    public bool IsAutoUpdateStatsAsyncOn { get; set; }

    // Parameterization — notable if on
    public bool IsParameterizationForced { get; set; }

    // Database-scoped configs (2016+/Azure)
    public List<ScopedConfigItem> NonDefaultScopedConfigs { get; set; } = new();
}

public class ScopedConfigItem
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string? ValueForSecondary { get; set; }
}
