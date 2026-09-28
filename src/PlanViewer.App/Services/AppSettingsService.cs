using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlanViewer.App.Services;

/// <summary>
/// Persists recent plans and open session state to a JSON file in the app's local data directory.
/// </summary>
internal sealed class AppSettingsService
{
    private const int MaxRecentPlans = 10;

    // Not readonly only because RedirectStorageForTestHost exists; nothing in the
    // product assigns these outside the static constructor.
    private static string SettingsDir;
    private static string SettingsPath;
    private static string OldFormatSettingsPath;
    private static string ScratchDir;

    private static AppSettings? _cached;

    /// <summary>
    /// Set by <see cref="Load"/> when <see cref="SettingsPath"/> exists but could not even be
    /// read (locked, permissions) — as opposed to missing, or present but unparseable, both of
    /// which are fine to overwrite. <see cref="Save"/> consults this so a transient read failure
    /// can never cost the user their file: it skips quietly, the same way it already does for a
    /// write failure.
    ///
    /// <para>It stays set for the rest of the process, even after a later <see cref="Load"/>
    /// reads the file. That failed Load handed out defaults, and callers keep what they are
    /// given: MainWindow holds its copy for the whole session and passes it to the Settings
    /// window, which saves a clone of it. Once the block lifted, either save would write those
    /// defaults over the user's file. So the session runs without saving, and the next start
    /// reads the file again.</para>
    /// </summary>
    private static bool _saveBlocked;

    static AppSettingsService()
    {
        SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerformanceStudio");
        SettingsPath = Path.Combine(SettingsDir, "appsettings.json");
        OldFormatSettingsPath = Path.Combine(SettingsDir, "perfstudio_format_settings.json");
        ScratchDir = Path.Combine(SettingsDir, "scratch");
    }

    /// <summary>
    /// The file settings are read from and written to right now — the real per-user path
    /// unless <see cref="RedirectStorageForTestHost"/> moved it. Exposed so the test suite
    /// can pin that the redirection actually happened (#451) instead of trusting it.
    /// </summary>
    internal static string SettingsFilePath => SettingsPath;

    /// <summary>
    /// Where scratch query buffers live (#496): one file per never-saved query tab, so an
    /// abnormal exit does not take typed-but-unsaved work with it. Beside the settings file
    /// rather than anywhere fancier because it is the same class of state — and, exactly like
    /// <see cref="SettingsFilePath"/>, it rides <see cref="RedirectStorageForTestHost"/>, so
    /// tests that exercise the real buffer writes land them in the run-scoped temp root
    /// instead of the developer's profile (#487's pattern, same reasoning as #451).
    /// </summary>
    internal static string ScratchDirectory => ScratchDir;

    /// <summary>
    /// Points every settings read and write at <paramref name="directory"/> instead of the
    /// real per-user profile. Exists for exactly one caller: the test harness (#451). Its
    /// MainWindow-driving tests run the real settings code — RestoreOpenPlans clears and
    /// saves the open-tab list, LoadPlanFile saves Recent Plans — and were doing all of it
    /// against the developer's actual appsettings.json: fixture paths evicted real recent
    /// entries and the saved session-restore list was destroyed, confirmed live. When this
    /// is never called, the paths keep their static-constructor defaults byte for byte, so
    /// the real app is untouched.
    /// </summary>
    internal static void RedirectStorageForTestHost(string directory)
    {
        SettingsDir = directory;
        SettingsPath = Path.Combine(directory, "appsettings.json");
        OldFormatSettingsPath = Path.Combine(directory, "perfstudio_format_settings.json");
        ScratchDir = Path.Combine(directory, "scratch");

        // Anything cached was loaded from the old location; drop it so the first Load
        // after the redirect reads the new one. A block belongs to the old path too — the
        // new one hasn't been read yet, let alone failed to read.
        _cached = null;
        _saveBlocked = false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Options used by <see cref="AppSettings.Clone"/> — includes nulls so the roundtrip is lossless.
    /// </summary>
    internal static readonly JsonSerializerOptions CloneOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// Loads settings from disk. Returns default settings if the file is missing, corrupt, or
    /// unreadable. Migrates legacy format settings from the old standalone file if present.
    /// </summary>
    /// <remarks>
    /// Returns the in-process cached instance — callers must not mutate it without immediately
    /// <see cref="Save"/>ing it back (mutate-then-Save is how the rest of this class persists
    /// state; see <see cref="AppSettings.Clone"/> instead when the edit must stay a draft, e.g.
    /// the Settings dialog's Cancel button). A file that could not be read (as opposed to missing
    /// or unparseable — see <see cref="SettingsFileStore"/>) is never cached: every call keeps
    /// retrying the disk, so a caller that loads after a transient lock clears still gets the
    /// user's settings. <see cref="Save"/> stays refused for the rest of the process, though;
    /// see <see cref="_saveBlocked"/>.
    /// </remarks>
    public static AppSettings Load()
    {
        if (_cached != null)
            return _cached;

        try
        {
            var outcome = SettingsFileStore.Read<AppSettings>(
                SettingsPath,
                nameof(AppSettingsService),
                json => JsonSerializer.Deserialize<AppSettings>(json, JsonOptions),
                out var parsed);

            if (outcome == SettingsFileStore.ReadOutcome.Unreadable)
            {
                _saveBlocked = true;
                return new AppSettings();
            }

            var settings = parsed ?? new AppSettings();

            // Settings written before the open-tab list held queries use the old key.
            // Ahead of MigrateFormatSettings, which can Save mid-load and would otherwise
            // write the old key straight back out.
            MigrateOpenTabs(settings);

            // Migrate legacy format settings file into unified settings
            MigrateFormatSettings(settings);

            // Clamp numeric values to valid ranges
            settings.QueryStoreSlicerDays = Math.Clamp(settings.QueryStoreSlicerDays, 1, 365);
            settings.QueryStoreTopLimit = Math.Clamp(settings.QueryStoreTopLimit, 1, 200);
            settings.MultiQsTopDbCount = Math.Clamp(settings.MultiQsTopDbCount, 2, 20);
            settings.QueryHistoryMaxPlans = Math.Clamp(settings.QueryHistoryMaxPlans, 1, 100);

            // Migrate installs still on the old default palette to the validated
            // colorblind-safe ramp. Only touches settings that exactly match the
            // legacy defaults, so any user-customized colors are left untouched.
            if (settings.MultiQsTopDbColors.SequenceEqual(LegacyTopDbColors, StringComparer.OrdinalIgnoreCase))
                settings.MultiQsTopDbColors = new List<string>(DefaultTopDbColors);

            _cached = settings;
            return settings;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppSettings: failed to load settings: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Clears the in-process settings cache so the next <see cref="Load"/> re-reads from disk.
    /// </summary>
    public static void Invalidate() => _cached = null;

    /// <summary>
    /// True once a <see cref="Load"/> has found the settings file unreadable, which blocks every
    /// save for the rest of the process (see <see cref="_saveBlocked"/>). The Settings window
    /// checks it after saving, so it can say the save did not happen instead of closing as if
    /// it had.
    /// </summary>
    internal static bool SaveBlocked => _saveBlocked;

    /// <summary>What the Settings window shows when <see cref="SaveBlocked"/> is set.</summary>
    internal static string SaveBlockedMessage =>
        $"Settings were not saved. {SettingsPath} could not be read earlier in this session, and saving now could replace it with default settings. Restart Performance Studio, then save again.";

    /// <summary>
    /// Saves settings to disk. Silently ignores write failures — including a save attempted
    /// while <see cref="Load"/> found the file unreadable, which would otherwise overwrite
    /// content this process has never actually seen. The Settings window reports that case
    /// through <see cref="SaveBlocked"/>.
    ///
    /// <para>In a secondary instance (<see cref="SingleInstance.IsSecondaryInstance"/>) the
    /// saved open-tab list is kept as the disk has it. The whole file is written, so the
    /// settings object's list goes with it — and a secondary's copy is the one it read at its
    /// own startup, long out of date next to the list the owner rewrites on every tab change.
    /// Recent plans, the Settings dialog and the server-filter toggle all save through here.
    /// The list is taken off the disk first, so the write hands it back unchanged. Every other
    /// setting stays last-write-wins, which is what two instances on purpose has always meant
    /// (see Program.Main). If the file cannot be read there is no list to keep, so the save is
    /// skipped rather than written with a guess.</para>
    /// </summary>
    public static void Save(AppSettings settings)
    {
        if (_saveBlocked)
            return;

        try
        {
            if (SingleInstance.IsSecondaryInstance && !TryAdoptSavedOpenTabs(settings))
                return;

            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            AtomicFile.WriteAllText(SettingsPath, json);
            _cached = settings;
        }
        catch
        {
            // Best-effort persistence — don't crash the app
        }
    }

    /// <summary>
    /// Puts the open-tab list that is on disk right now into <paramref name="settings"/>, for a
    /// secondary instance about to write the whole file (see <see cref="Save"/>). Read the way
    /// <see cref="Load"/> reads, so a file that will not parse is moved aside rather than
    /// overwritten. False when the file exists but cannot be read — the caller must not write.
    /// </summary>
    private static bool TryAdoptSavedOpenTabs(AppSettings settings)
    {
        var outcome = SettingsFileStore.Read<AppSettings>(
            SettingsPath,
            nameof(AppSettingsService),
            json => JsonSerializer.Deserialize<AppSettings>(json, JsonOptions),
            out var onDisk);

        if (outcome == SettingsFileStore.ReadOutcome.Unreadable)
            return false;

        if (onDisk != null)
            MigrateOpenTabs(onDisk);

        // Missing, or unparseable and just moved aside: there is no list on disk to keep, and
        // an empty one is a truer thing to write than a stale one.
        settings.OpenTabs = onDisk?.OpenTabs ?? new List<string>();
        return true;
    }

    /// <summary>
    /// Moves an "open_plans" list written by an older version onto <see cref="AppSettings.OpenTabs"/>,
    /// so upgrading does not cost the user the tabs they had open. Only fills an empty OpenTabs:
    /// if both keys are somehow present, the current one wins.
    /// </summary>
    /// <remarks>
    /// Nothing is written here. The old key disappears from disk on the next ordinary save, which
    /// happens on the first restore, so a downgrade before that point still finds its list.
    /// </remarks>
    internal static void MigrateOpenTabs(AppSettings settings)
    {
        if (settings.LegacyOpenPlans is { Count: > 0 } legacy && settings.OpenTabs.Count == 0)
            settings.OpenTabs = legacy;

        settings.LegacyOpenPlans = null;
    }

    /// <summary>
    /// If the old perfstudio_format_settings.json exists, migrate it into AppSettings
    /// (when FormatOptions is not yet set) and delete the old file unconditionally.
    /// Note: this intentionally calls <see cref="Save"/> inside <see cref="Load"/> as a
    /// one-time migration step. If Save fails, the old file remains and migration retries
    /// on the next Load — acceptable because the window is small and self-healing.
    /// </summary>
    private static void MigrateFormatSettings(AppSettings settings)
    {
        try
        {
            if (!File.Exists(OldFormatSettingsPath))
                return;

            if (settings.FormatOptions == null)
            {
                var json = File.ReadAllText(OldFormatSettingsPath);
                var legacy = JsonSerializer.Deserialize<SqlFormatSettings>(json, JsonOptions);
                if (legacy != null)
                {
                    settings.FormatOptions = legacy;
                    Save(settings);
                }
            }

            // Delete the old file whether we migrated or FormatOptions was already set
            File.Delete(OldFormatSettingsPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppSettingsService: failed to migrate format settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds a file path to the recent plans list (most recent first).
    /// Deduplicates by full path (case-insensitive on Windows).
    /// </summary>
    public static void AddRecentPlan(AppSettings settings, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);

        // Remove any existing entry for this path
        settings.RecentPlans.RemoveAll(p =>
            string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));

        // Insert at the front
        settings.RecentPlans.Insert(0, fullPath);

        // Trim to max size
        if (settings.RecentPlans.Count > MaxRecentPlans)
            settings.RecentPlans.RemoveRange(MaxRecentPlans, settings.RecentPlans.Count - MaxRecentPlans);
    }

    /// <summary>
    /// Removes a specific path from the recent plans list.
    /// </summary>
    public static void RemoveRecentPlan(AppSettings settings, string filePath)
    {
        settings.RecentPlans.RemoveAll(p =>
            string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Default color palette for Multi QS Overview top databases. Eight hues from
    /// the validated colorblind-safe categorical ramp (dark surface), in fixed CVD
    /// order. Databases beyond the eighth fold into the neutral "Others" fill, so
    /// no invented ninth hue is ever needed.
    /// </summary>
    internal static readonly List<string> DefaultTopDbColors = new()
    {
        "#3987E5", "#199E70", "#C98500", "#008300",
        "#9085E9", "#E66767", "#D55181", "#D95926",
    };

    /// <summary>
    /// The pre-dataviz default palette. Retained only so <see cref="Load"/> can
    /// detect an installation still on the old defaults and migrate it to
    /// <see cref="DefaultTopDbColors"/> without clobbering a user's custom colors.
    /// </summary>
    private static readonly List<string> LegacyTopDbColors = new()
    {
        "#2EAEF1", "#F2994A", "#27AE60", "#9B51E0", "#EB5757",
        "#F2C94C", "#56CCF2", "#BB6BD9", "#E91E63", "#00BCD4",
    };
}

/// <summary>
/// Serializable settings model for the application.
/// </summary>
internal sealed class AppSettings
{
    // ── App State ────────────────────────────────────────────────────

    [JsonPropertyName("recent_plans")]
    public List<string> RecentPlans { get; set; } = new();

    /// <summary>
    /// Paths of the tabs that were open when the app last closed, reopened on the next start.
    /// Holds queries as well as plans, which is why it is no longer named for plans.
    /// </summary>
    [JsonPropertyName("open_tabs")]
    public List<string> OpenTabs { get; set; } = new();

    /// <summary>
    /// What <see cref="OpenTabs"/> was called before it held queries too. Read on load so an
    /// upgrade does not throw away the tabs the previous version wrote down, then nulled —
    /// nulls are not serialized, so the old key drops out of the file on the next save.
    /// </summary>
    [JsonPropertyName("open_plans")]
    public List<string>? LegacyOpenPlans { get; set; }

    /// <summary>
    /// Divergence limit for accuracy ratio coloring on plan links. Default 10.
    /// Links with accuracy ratio between 1/limit and limit keep the default edge color.
    /// </summary>
    [JsonPropertyName("accuracy_ratio_divergence_limit")]
    public double AccuracyRatioDivergenceLimit { get; set; } = 10;

    // ── Query Store Settings ─────────────────────────────────────────

    /// <summary>
    /// Number of days of Query Store data to load in the time-range slicer. Default 30.
    /// </summary>
    [JsonPropertyName("query_store_slicer_days")]
    public int QueryStoreSlicerDays { get; set; } = 30;

    /// <summary>
    /// Default metric for the top queries grid. Default "cpu" (= Total CPU).
    /// Values: cpu, avg-cpu, duration, avg-duration, reads, avg-reads,
    /// writes, avg-writes, physical-reads, avg-physical-reads, memory, avg-memory, executions.
    /// </summary>
    [JsonPropertyName("query_store_default_metric")]
    public string QueryStoreDefaultMetric { get; set; } = "cpu";

    /// <summary>
    /// Default number of top elements/groups shown in the grid. Default 25.
    /// </summary>
    [JsonPropertyName("query_store_top_limit")]
    public int QueryStoreTopLimit { get; set; } = 25;

    /// <summary>
    /// Default time range quick-filter selection (hours as string).
    /// Options: "3" (3h), "24" (24h), "48" (48h), "168" (7d), "720" (30d).
    /// </summary>
    [JsonPropertyName("query_store_default_time_range")]
    public string QueryStoreDefaultTimeRange { get; set; } = "24";

    /// <summary>
    /// Default time display mode: "Local", "Utc", or "Server".
    /// </summary>
    [JsonPropertyName("query_store_default_time_display")]
    public string QueryStoreDefaultTimeDisplay { get; set; } = "Local";

    /// <summary>
    /// Default group-by mode: "None", "QueryHash", or "Module".
    /// </summary>
    [JsonPropertyName("query_store_default_group_by")]
    public string QueryStoreDefaultGroupBy { get; set; } = "QueryHash";

    /// <summary>
    /// Whether the Query Store server-filter panel is expanded. Default false (collapsed).
    /// </summary>
    [JsonPropertyName("query_store_filter_panel_expanded")]
    public bool QueryStoreFilterPanelExpanded { get; set; }

    // ── Multi QS Overview Settings ───────────────────────────────────

    /// <summary>
    /// Number of top databases shown in the overview. Default 5, min 2, max 20.
    /// </summary>
    [JsonPropertyName("multi_qs_top_db_count")]
    public int MultiQsTopDbCount { get; set; } = 5;

    /// <summary>
    /// Hex color codes for top databases in the overview chart.
    /// </summary>
    [JsonPropertyName("multi_qs_top_db_colors")]
    public List<string> MultiQsTopDbColors { get; set; } = new(AppSettingsService.DefaultTopDbColors);

    // ── Query History Settings ───────────────────────────────────────

    /// <summary>
    /// Default metric for the query history chart. Default "AvgDurationMs".
    /// </summary>
    [JsonPropertyName("query_history_default_metric")]
    public string QueryHistoryDefaultMetric { get; set; } = "AvgDurationMs";

    /// <summary>
    /// Maximum number of plans fetched for a query history. Default 10, min 1, max 100.
    /// </summary>
    [JsonPropertyName("query_history_max_plans")]
    public int QueryHistoryMaxPlans { get; set; } = 10;

    // ── Script Options (Format) ──────────────────────────────────────

    /// <summary>
    /// SQL format options. Null means use <see cref="SqlFormatSettings"/> defaults.
    /// </summary>
    [JsonPropertyName("format_options")]
    public SqlFormatSettings? FormatOptions { get; set; }

    /// <summary>
    /// Creates a deep copy via JSON roundtrip so mutations don't leak to callers.
    /// </summary>
    internal AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, AppSettingsService.CloneOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, AppSettingsService.CloneOptions) ?? new AppSettings();
    }
}
