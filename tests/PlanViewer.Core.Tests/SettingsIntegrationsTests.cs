using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Settings &gt; Integrations, and the save bug that moving MCP and proxy there exposed.
///
/// <para>The MCP port and proxy configuration used to live in the About box. They are settings,
/// not facts about the build, and nobody goes looking for a port number behind "About" — so they
/// moved into Settings as their own section. These pin both halves: that About no longer carries
/// them, and that Settings does.</para>
///
/// <para><b>Read this before adding a test that saves.</b> <see cref="SettingsFile"/> is
/// redirected to a temp directory by the harness, the same as appsettings.json — so the JSON
/// half of a save is safe here. <b>The OS credential store is not redirected.</b>
/// <c>CredentialServiceFactory</c> has no test hook and hands back the real Windows credential
/// service, so anything that reaches <c>ProxySettings.Save</c> with <c>TouchCredential</c> set
/// writes or deletes the developer's actual stored proxy password.</para>
///
/// <para>Two things keep that from happening today, and both are load-bearing: the production
/// code only touches the credential store on a positive instruction (a typed password, or a
/// Reset asking for the stored one to go), and no test here types one or saves after a Reset.
/// If you need to test that path, give the factory a test hook first.</para>
/// </summary>
public class SettingsIntegrationsTests
{
    [Fact]
    public void SavingWithoutVisitingQueryHistoryKeepsItsSettings()
    {
        /* The regression. Each section's controls are built only when the user first opens that
           section, and Save read the values back off whatever controls happened to exist. Query
           History was read unguarded, so a null combo came back as "" and a null spinner as its
           hardcoded 10 — meaning the ordinary act of changing a Query Store option and pressing
           Save silently reset both Query History settings. Format Options was already guarded
           against exactly this; Query History was not. */
        HeadlessUi.Run(() =>
        {
            var settings = new AppSettings
            {
                QueryHistoryDefaultMetric = "TotalCpuMs",
                QueryHistoryMaxPlans = 50
            };

            var window = new SettingsWindow(settings);
            AppSettings? saved = null;
            window.SettingsSaved += s => saved = s;

            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Save straight from the section that opens first, never visiting Query History.
            var saveButton = window.FindControl<Button>("SaveButton")!;
            saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(saved);
            Assert.Equal("TotalCpuMs", saved!.QueryHistoryDefaultMetric);
            Assert.Equal(50, saved.QueryHistoryMaxPlans);
        });
    }

    [Fact]
    public void IntegrationsSectionCarriesTheMcpAndProxySettings()
    {
        HeadlessUi.Run(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sections = window.FindControl<ListBox>("SectionList")!;
            sections.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();

            var detail = (Control)window.FindControl<ContentControl>("DetailPanel")!.Content!;
            var headings = detail.GetLogicalDescendants().OfType<TextBlock>()
                                 .Select(t => t.Text).ToList();

            Assert.Contains("MCP Server", headings);
            Assert.Contains("Proxy", headings);

            // The MCP enable toggle and both proxy modes, by the text the user reads.
            Assert.Contains(detail.GetLogicalDescendants().OfType<CheckBox>(),
                c => c.Content as string == "Enable MCP server");
            Assert.Equal(2, detail.GetLogicalDescendants().OfType<RadioButton>().Count());

            window.Close();
        });
    }

    [Fact]
    public void ChoosingManualRevealsTheProxyFieldsAndMarksTheDialogDirty()
    {
        HeadlessUi.Run(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.FindControl<ListBox>("SectionList")!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();

            var detail = (Control)window.FindControl<ContentControl>("DetailPanel")!.Content!;
            var manual = detail.GetLogicalDescendants().OfType<RadioButton>()
                               .Single(r => r.Content as string == "Manual");

            /* The address/username/password grid is collapsed for a system proxy. Finding it by
               the inputs it holds rather than by name, because it is built in code and has no
               x:Name to reach for. */
            var fields = detail.GetLogicalDescendants().OfType<TextBox>().ToList();
            Assert.Equal(3, fields.Count);
            Assert.All(fields, f => Assert.False(f.IsEffectivelyVisible));
            Assert.False(IsDirty(window), "building the section must not look like an edit");

            manual.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.All(fields, f => Assert.True(f.IsEffectivelyVisible));
            Assert.True(IsDirty(window), "choosing a proxy mode is an edit");

            CloseWithoutPrompting(window);
        });
    }

    [Fact]
    public void ResetAllFromAnotherSectionStillResetsQueryStore()
    {
        /* The other half of the save bug above, and the same root cause: Reset All replaces the
           settings object but rebuilds only the section on screen, so the other sections' controls
           survive holding pre-reset values and Save reads them back over the defaults. Performed
           from Integrations, a Reset All used to leave Query Store exactly as it was. */
        HeadlessUi.Run(() =>
        {
            var settings = new AppSettings { QueryStoreSlicerDays = 90, QueryStoreTopLimit = 99 };
            var fresh = new AppSettings();

            var window = new SettingsWindow(settings);
            AppSettings? saved = null;
            window.SettingsSaved += s => saved = s;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Stand somewhere other than Query Store, then reset everything.
            window.FindControl<ListBox>("SectionList")!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            window.FindControl<Button>("ResetAllButton")!
                  .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            window.FindControl<Button>("SaveButton")!
                  .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(saved);
            Assert.Equal(fresh.QueryStoreSlicerDays, saved!.QueryStoreSlicerDays);
            Assert.Equal(fresh.QueryStoreTopLimit, saved.QueryStoreTopLimit);
            Assert.NotEmpty(saved.MultiQsTopDbColors);
        });
    }

    [Fact]
    public void AboutNoLongerAsksForAnyConfiguration()
    {
        /* About is now version, copyright, links and the update check. If a settings control
           reappears here, it has been put back in the place this move took it out of. */
        HeadlessUi.Run(() =>
        {
            var about = new AboutWindow();
            about.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(about.GetLogicalDescendants().OfType<TextBox>());
            Assert.Empty(about.GetLogicalDescendants().OfType<CheckBox>());
            Assert.Empty(about.GetLogicalDescendants().OfType<RadioButton>());

            about.Close();
        });
    }

    [Fact]
    public void McpSettingsReadThroughTheSameFileIntegrationsWritesTo()
    {
        /* The redirect is only worth having if both directions honour it. McpSettings.Load used
           to rebuild the real ~/.planview path itself, so under test it read the developer's own
           file while every write went to the temp one — a split-brain that is worse than either
           half alone, and invisible until a value disagrees with itself. Writing through
           SettingsFile and reading it back proves the two now agree on where the file is. */
        SettingsFile.Update(o =>
        {
            o["mcp_enabled"] = true;
            o["mcp_port"] = 5999;
        });

        var mcp = McpSettings.Load();

        Assert.True(mcp.Enabled);
        Assert.Equal(5999, mcp.Port);
        Assert.StartsWith(Path.GetTempPath(), SettingsFile.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMalformedMcpKeyCostsOnlyItself()
    {
        SettingsFile.Update(o =>
        {
            o["mcp_enabled"] = true;
            o["mcp_port"] = "not a port";
        });

        var mcp = McpSettings.Load();

        Assert.True(mcp.Enabled);       // survives its neighbour being junk
        Assert.Equal(5152, mcp.Port);   // and the junk falls back rather than throwing
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// The dialog's unsaved-changes flag. Read by reflection rather than exposed, because it is
    /// nobody's business outside the window and a test-only property would be worse.
    /// </summary>
    private static bool IsDirty(SettingsWindow window) =>
        (bool)typeof(SettingsWindow)
            .GetField("_isDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    /// <summary>
    /// Closes a dirty dialog without the discard prompt. Closing one for real cancels the close
    /// and awaits a modal nobody is here to answer, which would leave a live window behind in the
    /// headless app every other test shares.
    /// </summary>
    private static void CloseWithoutPrompting(SettingsWindow window)
    {
        typeof(SettingsWindow)
            .GetField("_isDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, false);
        window.Close();
    }
}
