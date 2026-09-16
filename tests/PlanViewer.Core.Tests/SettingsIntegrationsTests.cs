using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Settings &gt; Integrations, and the save bug that moving MCP and proxy there exposed.
///
/// <para>The MCP port and proxy configuration used to live in the About box. They are settings,
/// not facts about the build, and nobody goes looking for a port number behind "About" — so they
/// moved into Settings as their own section. These pin both halves: that About no longer carries
/// them, and that Settings does.</para>
///
/// <para>All three stores this section touches are redirected by the harness: appsettings.json,
/// the <see cref="SettingsFile"/> JSON holding the MCP port and proxy configuration, and the OS
/// credential manager holding the proxy password. That last one matters most — a save here can
/// delete a credential, and before it was redirected the only thing standing between a test and
/// the developer's real stored password was nobody having written that test yet.</para>
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
        /* Start from a known proxy mode. The redirected settings file is shared for the whole run,
           and a sibling test saves a Manual proxy into it — so this test's "the fields start
           hidden" premise depended on which order the two happened to run in. */
        SettingsFile.Update(o => o["proxy_mode"] = "system");

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
    public void AnEditSurvivesLeavingItsSectionAndComingBack()
    {
        /* Sections build their controls on first visit and are thrown away on the way out. While
           Save read values back off whatever controls existed, an edit lived only in the control:
           leave the section and the control was orphaned, come back and a fresh one was built from
           the unchanged settings, and the edit was gone — while the dialog still believed itself
           dirty and would ask to discard changes that no longer existed. Every section writes into
           the settings as it is edited now, so this is a round trip rather than a reset. */
        HeadlessUi.Run(() =>
        {
            var window = new SettingsWindow(new AppSettings { QueryStoreSlicerDays = 30 });
            AppSettings? saved = null;
            window.SettingsSaved += s => saved = s;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sections = window.FindControl<ListBox>("SectionList")!;
            var detail = window.FindControl<ContentControl>("DetailPanel")!;

            var slicerDays = FindByRowLabel<NumericUpDown>(detail, "Default history length (days)");
            slicerDays.Value = 45;
            Dispatcher.UIThread.RunJobs();

            // Away...
            sections.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            // ...and back.
            sections.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();

            var rebuilt = FindByRowLabel<NumericUpDown>(detail, "Default history length (days)");
            Assert.Equal(45m, rebuilt.Value);

            window.FindControl<Button>("SaveButton")!
                  .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(saved);
            Assert.Equal(45, saved!.QueryStoreSlicerDays);
        });
    }

    [Fact]
    public void EditingDoesNotReachTheLiveSettingsUntilSave()
    {
        /* Now that sections write into _settings as they are edited, _settings has to be a draft
           rather than the configuration the rest of the app is reading. The parameterless
           constructor takes AppSettingsService.Load(), which hands back a cached instance shared
           process-wide, so without a copy a single keystroke in this dialog would already have
           changed the app's settings and Cancel would have nothing left to undo. */
        HeadlessUi.Run(() =>
        {
            var before = AppSettingsService.Load().QueryStoreSlicerDays;

            var window = new SettingsWindow();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var detail = window.FindControl<ContentControl>("DetailPanel")!;
            FindByRowLabel<NumericUpDown>(detail, "Default history length (days)").Value = before + 7;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(before, AppSettingsService.Load().QueryStoreSlicerDays);

            /* Both halves, or this passes when the edit never lands at all — which is exactly what
               a stuck _building flag would do, and it would look identical from the cache's side. */
            Assert.True(IsDirty(window), "the draft must have taken the edit the cache did not");

            CloseWithoutPrompting(window);
        });
    }

    [Fact]
    public void ChangingTheDatabaseCountResizesTheStoredColours()
    {
        /* The colour rows are rebuilt from the stored list whenever the count changes, so the two
           have to agree. They used to only meet at save time, where the list was rebuilt from
           whatever boxes existed — which meant editing a colour and then changing the count threw
           the edit away. */
        HeadlessUi.Run(() =>
        {
            var window = new SettingsWindow(new AppSettings { MultiQsTopDbCount = 5 });
            AppSettings? saved = null;
            window.SettingsSaved += s => saved = s;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var detail = window.FindControl<ContentControl>("DetailPanel")!;
            var dbCount = FindByRowLabel<NumericUpDown>(detail, "Number of top databases");

            dbCount.Value = 8;
            Dispatcher.UIThread.RunJobs();

            var boxes = ((Control)detail.Content!).GetLogicalDescendants().OfType<TextBox>().ToList();
            Assert.Equal(8, boxes.Count);

            boxes[7].Text = "#123456";
            Dispatcher.UIThread.RunJobs();

            // Shrink past that row and grow back. The colour has to survive the trip: the stored
            // list is the only record of it, so trimming the list to the count would lose it and
            // hand back a stock colour instead.
            dbCount.Value = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, ((Control)detail.Content!).GetLogicalDescendants().OfType<TextBox>().Count());

            dbCount.Value = 8;
            Dispatcher.UIThread.RunJobs();

            var regrown = ((Control)detail.Content!).GetLogicalDescendants().OfType<TextBox>().ToList();
            Assert.Equal("#123456", regrown[7].Text);

            window.FindControl<Button>("SaveButton")!
                  .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(saved);
            Assert.Equal(8, saved!.MultiQsTopDbCount);
            Assert.Equal("#123456", saved.MultiQsTopDbColors[7]);
        });
    }

    [Fact]
    public void AnInvalidColourRefusesTheSaveAndShowsWhichOne()
    {
        /* Saving used to validate the text boxes themselves. From any other section those boxes
           were detached, so the refusal reddened controls nobody could see and the dialog just
           silently declined to close. The colours are checked in the settings now, and an invalid
           one brings its own section forward to be corrected. */
        HeadlessUi.Run(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            var saves = 0;
            window.SettingsSaved += _ => saves++;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sections = window.FindControl<ListBox>("SectionList")!;
            var detail = window.FindControl<ContentControl>("DetailPanel")!;

            ((Control)detail.Content!).GetLogicalDescendants().OfType<TextBox>().First()
                .Text = "not a colour";
            Dispatcher.UIThread.RunJobs();

            // Walk away, so the offending boxes are detached, then try to save.
            sections.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            window.FindControl<Button>("SaveButton")!
                  .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, saves);
            Assert.Equal(0, sections.SelectedIndex);

            var offending = ((Control)detail.Content!).GetLogicalDescendants()
                                                      .OfType<TextBox>().First();
            Assert.Equal("not a colour", offending.Text);
            Assert.Equal(Brushes.Red, offending.BorderBrush);

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

    [Fact]
    public void ResetAllLeavesTheSavedProxyPasswordAlone()
    {
        /* Reset All is one unconfirmed button reachable from every section, and deleting an OS
           credential is the only thing in this dialog that Cancel cannot really undo. Someone
           restoring the Query Store defaults has not asked to lose their proxy password. Reset
           Section, pressed on Integrations, is the gesture that means that. */
        // There has to be one for either button to have anything to remove.
        CredentialServiceFactory.Create().SaveCredential("__proxy__", "someone", "reset-me");

        HeadlessUi.Run(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.FindControl<Button>("ResetAllButton")!
                  .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(StagesCredentialDeletion(window),
                "Reset All must not take a credential the user never came here for");

            // The same button on the section itself is the gesture that does mean that.
            window.FindControl<ListBox>("SectionList")!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            window.FindControl<Button>("ResetButton")!
                  .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(StagesCredentialDeletion(window),
                "Reset Section on Integrations is how a stored password is removed");

            CloseWithoutPrompting(window);
        });
    }

    [Fact]
    public void OnlyAPositiveInstructionEverTouchesTheStoredPassword()
    {
        /* The bug this guards: ProxySettings.Load swallows a credential-store failure and reports
           no password, which is indistinguishable from there being none — and the old rule
           ("touch the credential unless the box is empty AND one is stored") then deleted a
           password nobody had touched. Moving these settings widened the trigger from a proxy
           field losing focus to any save at all, so ticking the MCP checkbox was enough.

           What this pins is the invariant, not that original scenario: an ordinary save leaves
           the stored password alone. Reproducing the scenario itself needs a credential store
           that fails its reads, and the in-memory one the harness installs cannot fail — so the
           first half of the old rule stays untested here and only the conclusion is guarded.
           Mutating TouchCredential to an unconditional true fails this test, which is the
           regression a future simplification would actually introduce. */
        var credentials = CredentialServiceFactory.Create();
        credentials.SaveCredential("__proxy__", "someone", "kept-secret");

        HeadlessUi.Run(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.FindControl<ListBox>("SectionList")!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();

            var detail = (Control)window.FindControl<ContentControl>("DetailPanel")!.Content!;

            /* Two different protections, so exercise both in one pass.

               Changing only MCP must not reach the credential store at all — that is the split
               between the two writes. Changing a proxy field does reach it, and must still leave
               the password alone, because an empty password box is not an instruction to delete
               anything. The second case is the one that used to destroy a credential whenever
               the store had been unreadable at load. */
            var mcpToggle = detail.GetLogicalDescendants().OfType<CheckBox>().First();
            mcpToggle.IsChecked = mcpToggle.IsChecked != true;

            var manual = detail.GetLogicalDescendants().OfType<RadioButton>()
                               .Single(r => r.Content as string == "Manual");
            manual.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            var address = detail.GetLogicalDescendants().OfType<TextBox>().First();
            address.Text = "http://proxy.internal:3128";
            Dispatcher.UIThread.RunJobs();

            window.FindControl<Button>("SaveButton")!
                  .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        });

        var after = credentials.GetCredential("__proxy__");
        Assert.True(after.HasValue, "an unrelated save must not delete the saved proxy password");
        Assert.Equal("kept-secret", after!.Value.Password);
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// The control on the row carrying <paramref name="label"/>. CreateRow lays each setting out
    /// as a label above its control, so this finds one the way a reader would rather than by
    /// counting spinners — which breaks the moment a row is added above it.
    /// </summary>
    private static T FindByRowLabel<T>(ContentControl detail, string label) where T : Control =>
        ((Control)detail.Content!).GetLogicalDescendants().OfType<StackPanel>()
            .Where(row => row.Children.Count >= 2
                       && row.Children[0] is TextBlock caption
                       && caption.Text == label)
            .Select(row => row.Children[1])
            .OfType<T>()
            .First();

    /// <summary>Whether a Save from here would remove the stored proxy password.</summary>
    private static bool StagesCredentialDeletion(SettingsWindow window) =>
        (bool)typeof(SettingsWindow)
            .GetField("_clearStoredProxyPassword", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

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
