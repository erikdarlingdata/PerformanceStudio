using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Dialogs;
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
/// <para>A note for whoever adds to this file: <see cref="SettingsFile"/> is redirected to a
/// temp directory by the harness, the same as appsettings.json. That redirect exists because
/// the proxy save path writes the real <c>~/.planview/settings.json</c> and the OS credential
/// store, so a test that drives Save on a loaded Integrations section would otherwise rewrite
/// the developer's own configuration.</para>
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

            manual.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.All(fields, f => Assert.True(f.IsEffectivelyVisible));

            window.Close();
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
}
