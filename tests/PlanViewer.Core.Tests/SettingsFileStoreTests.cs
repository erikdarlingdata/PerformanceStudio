using System;
using System.Collections.Generic;
using System.IO;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The shared corrupt/unreadable-file policy (<see cref="SettingsFileStore"/>) as it applies to
/// each of the three stores that use it. Before this policy existed, all three treated "can't
/// parse" and "can't read" the same as "does not exist" — defaults came back, and the very next
/// save silently overwrote the user's file with those defaults. That is exactly backward for a
/// read failure that might be transient: the file deserves a chance to be read again, not to be
/// clobbered with a guess.
///
/// <para>Every test writes and locks only the redirected files under
/// <see cref="HeadlessUi.SettingsRedirectRoot"/> — see the module initializer that sets that up
/// for the whole run. None of this touches the developer's real profile.</para>
/// </summary>
[Collection("SettingsFileStore serial")]
public class SettingsFileStoreTests
{
    // ── AppSettingsService ──────────────────────────────────────────────

    [Fact]
    public void MalformedAppSettingsIsQuarantinedAndDefaultsComeBackAndSavesWork()
    {
        var path = AppSettingsService.SettingsFilePath;
        File.WriteAllText(path, "{ not valid json");
        AppSettingsService.Invalidate();

        var settings = AppSettingsService.Load();
        Assert.Equal(30, settings.QueryStoreSlicerDays); // the default — the broken file must not throw

        Assert.False(File.Exists(path), "the unparseable file must be moved aside, not left in place");
        var quarantined = FindQuarantineFiles(path);
        Assert.Single(quarantined);
        Assert.Contains("not valid json", File.ReadAllText(quarantined[0]));

        // Saves work normally right after — quarantining is not the same as being blocked.
        settings.QueryStoreSlicerDays = 77;
        AppSettingsService.Save(settings);
        AppSettingsService.Invalidate();
        Assert.Equal(77, AppSettingsService.Load().QueryStoreSlicerDays);

        File.Delete(quarantined[0]);
    }

    [Fact]
    public void MissingAppSettingsFileIsDefaultsAndSavesWork()
    {
        var path = AppSettingsService.SettingsFilePath;
        if (File.Exists(path))
            File.Delete(path);
        AppSettingsService.Invalidate();

        var settings = AppSettingsService.Load();
        Assert.Equal(30, settings.QueryStoreSlicerDays);

        settings.QueryStoreSlicerDays = 55;
        AppSettingsService.Save(settings);
        AppSettingsService.Invalidate();
        Assert.Equal(55, AppSettingsService.Load().QueryStoreSlicerDays);
    }

    [Fact]
    public void UnreadableAppSettingsRefusesToSaveUntilALaterReadSucceeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Unix permissions don't reliably block a same-user read the way FileShare.None does on Windows.");

        var path = AppSettingsService.SettingsFilePath;
        const string original = """{"query_store_slicer_days": 12}""";
        File.WriteAllText(path, original);
        AppSettingsService.Invalidate();

        AppSettings blocked;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            blocked = AppSettingsService.Load();
            Assert.Equal(30, blocked.QueryStoreSlicerDays); // couldn't read the real 12 — defaults
        }

        /* The lock is released before the save on purpose. While it is held, the rename onto the
           locked file fails by itself, so a refusal seen there proves nothing about the block.
           Now the file is readable again but nothing has read it successfully since, so only the
           block stops this save from replacing the user's 12 with a guess. */
        blocked.QueryStoreSlicerDays = 999;
        AppSettingsService.Save(blocked); // must not throw, and must not touch the file
        Assert.Equal(original, File.ReadAllText(path));

        // A later read succeeds, the block clears, and saves resume.
        AppSettingsService.Invalidate();
        var reloaded = AppSettingsService.Load();
        Assert.Equal(12, reloaded.QueryStoreSlicerDays);

        reloaded.QueryStoreSlicerDays = 88;
        AppSettingsService.Save(reloaded);
        AppSettingsService.Invalidate();
        Assert.Equal(88, AppSettingsService.Load().QueryStoreSlicerDays);
    }

    // ── ConnectionStore ──────────────────────────────────────────────────

    [Fact]
    public void MalformedConnectionsFileIsQuarantinedAndDefaultsComeBackAndSavesWork()
    {
        var path = ConnectionStore.ConfigFilePath;
        File.WriteAllText(path, "{ not valid json");
        var store = new ConnectionStore();

        var loaded = store.Load();
        Assert.Empty(loaded);

        var quarantined = FindQuarantineFiles(path);
        Assert.Single(quarantined);
        Assert.Contains("not valid json", File.ReadAllText(quarantined[0]));

        store.Save(new List<ServerConnection> { new() { ServerName = "svr" } });
        Assert.Single(store.Load());

        File.Delete(quarantined[0]);
    }

    [Fact]
    public void MissingConnectionsFileIsDefaultsAndSavesWork()
    {
        var path = ConnectionStore.ConfigFilePath;
        if (File.Exists(path))
            File.Delete(path);
        var store = new ConnectionStore();

        Assert.Empty(store.Load());

        store.Save(new List<ServerConnection> { new() { ServerName = "svr" } });
        Assert.Single(store.Load());
    }

    [Fact]
    public void UnreadableConnectionsFileRefusesToSaveUntilALaterReadSucceeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Unix permissions don't reliably block a same-user read the way FileShare.None does on Windows.");

        var path = ConnectionStore.ConfigFilePath;
        var original = """[{"ServerName":"kept-server"}]""";
        File.WriteAllText(path, original);
        var store = new ConnectionStore();

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(store.Load()); // couldn't read the real list — defaults
        }

        /* Released before the save on purpose: while the lock is held, the rename onto the file
           fails by itself, so a refusal seen there proves nothing about the block. Now the file
           is readable again but nothing has read it successfully since, so only the block can
           stop this save. ConnectionStore.Save throws rather than swallowing, so its callers (the
           connection dialog) hear about it instead of silently losing the saved server. */
        var ex = Assert.Throws<IOException>(
            () => store.Save(new List<ServerConnection> { new() { ServerName = "guess" } }));
        Assert.Contains("could not be read on the last load", ex.Message);
        Assert.Contains(path, ex.Message);
        Assert.Equal(original, File.ReadAllText(path));

        // A later read succeeds, the block clears, and saves resume.
        var reloaded = store.Load();
        Assert.Single(reloaded);
        Assert.Equal("kept-server", reloaded[0].ServerName);

        store.Save(new List<ServerConnection> { new() { ServerName = "kept-server" }, new() { ServerName = "new-server" } });
        Assert.Equal(2, store.Load().Count);
    }

    // ── SettingsFile ─────────────────────────────────────────────────────

    [Fact]
    public void MalformedSettingsFileIsQuarantinedAndDefaultsComeBackAndUpdatesWork()
    {
        var path = SettingsFile.Path;
        File.WriteAllText(path, "{ not valid json");

        var obj = SettingsFile.Read();
        Assert.Empty(obj);

        var quarantined = FindQuarantineFiles(path);
        Assert.Single(quarantined);
        Assert.Contains("not valid json", File.ReadAllText(quarantined[0]));

        SettingsFile.Update(o => o["mcp_port"] = 5555);
        Assert.Equal(5555, SettingsFile.Read()["mcp_port"]!.GetValue<int>());

        File.Delete(quarantined[0]);
    }

    /// <summary>
    /// <see cref="JsonNode.Parse"/> succeeds on a top-level array — it just isn't a
    /// <see cref="System.Text.Json.Nodes.JsonObject"/>, which the old
    /// <c>as JsonObject ?? new JsonObject()</c> cast silently treated as "empty file" rather than
    /// as the wrong-shape document it actually is.
    /// </summary>
    [Fact]
    public void ArrayShapedSettingsFileIsTreatedAsMalformedNotEmpty()
    {
        var path = SettingsFile.Path;
        File.WriteAllText(path, "[1,2,3]");

        var obj = SettingsFile.Read();
        Assert.Empty(obj);

        var quarantined = FindQuarantineFiles(path);
        Assert.Single(quarantined);
        Assert.Equal("[1,2,3]", File.ReadAllText(quarantined[0]));

        File.Delete(quarantined[0]);
    }

    [Fact]
    public void MissingSettingsFileIsDefaultsAndUpdatesWork()
    {
        var path = SettingsFile.Path;
        if (File.Exists(path))
            File.Delete(path);

        Assert.Empty(SettingsFile.Read());

        SettingsFile.Update(o => o["mcp_port"] = 6111);
        Assert.Equal(6111, SettingsFile.Read()["mcp_port"]!.GetValue<int>());
    }

    [Fact]
    public void UnreadableSettingsFileRefusesToUpdateUntilALaterReadSucceeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Unix permissions don't reliably block a same-user read the way FileShare.None does on Windows.");

        var path = SettingsFile.Path;
        const string original = """{"mcp_port": 4321}""";
        File.WriteAllText(path, original);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(SettingsFile.Read()); // couldn't read the real value — defaults

            /* Update is read-modify-write, and it reads first, so its own read is what finds the
               file unreadable. Refusing here is what stops a blocked read from dropping every
               OTHER key (proxy settings included) on the next Update. Unlike the other two stores,
               this can only be seen under the lock: once it is released, Update's own read
               succeeds and the write is rightly allowed. The message is what tells this refusal
               apart from the raw sharing violation the rename would hit anyway. */
            var ex = Assert.Throws<IOException>(() => SettingsFile.Update(o => o["mcp_port"] = 9999));
            Assert.Contains("could not be read on the last load", ex.Message);
            Assert.Contains(path, ex.Message);
        }

        Assert.Equal(original, File.ReadAllText(path));

        // The lock is gone — a later read succeeds, the block clears, and updates resume.
        Assert.Equal(4321, SettingsFile.Read()["mcp_port"]!.GetValue<int>());

        SettingsFile.Update(o => o["mcp_port"] = 4322);
        Assert.Equal(4322, SettingsFile.Read()["mcp_port"]!.GetValue<int>());
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string[] FindQuarantineFiles(string originalPath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(originalPath)!,
            Path.GetFileName(originalPath) + ".bad-*");
}
