using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading; // Dispatcher.UIThread.RunJobs — see HeadlessUi's class doc for why
using PlanViewer.App.Dialogs;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Two windows save through a store that can now throw IOException when it refuses to overwrite
/// a file it could not read (see SettingsFileStore, ConnectionStore.Save and
/// SettingsFile.Update). Both used to let that exception escape uncaught — an async void click
/// handler for the connection dialog, a synchronous one for Settings — which crashes the app
/// either way. These pin that both catch it, show the message where each window already reports
/// errors, and leave the window open rather than closing on a failed save.
///
/// <para>Shares a collection with <see cref="SettingsFileStoreTests"/> and
/// <see cref="SettingsIntegrationsTests"/> for the same reason those two do: this locks the same
/// redirected connections.json/settings.json files, and xunit runs different collections in
/// parallel by default.</para>
/// </summary>
[Collection("SettingsFileStore serial")]
public class SaveFailureDisplayTests
{
    /// <summary>
    /// Connect_Click opens a real SQL connection before it ever reaches the store save, so it
    /// can't be driven headlessly without a live server. ConnectionDialog.TrySaveConnection is
    /// the small piece that actually does the save-and-report-failure work; this drives that
    /// directly, which is the save path the fix touches.
    /// </summary>
    [Fact]
    public void AnUnreadableConnectionsFileShowsItsMessageAndTheDialogStaysOpen()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Unix permissions don't reliably block a same-user read the way FileShare.None does on Windows.");

        var path = ConnectionStore.ConfigFilePath;
        const string original = """[{"ServerName":"kept-server"}]""";
        File.WriteAllText(path, original);

        HeadlessUi.Run(() =>
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            var dialog = new ConnectionDialog(CredentialServiceFactory.Create(), new ConnectionStore());
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            var saved = (bool)typeof(ConnectionDialog)
                .GetMethod("TrySaveConnection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, [new ServerConnection { ServerName = "new-server" }])!;

            Assert.False(saved, "a refused save must be reported, not treated as success");

            var status = dialog.FindControl<TextBlock>("StatusText")!;
            Assert.False(string.IsNullOrEmpty(status.Text), "the refusal must be visible somewhere");
            Assert.Contains(path, status.Text);
            Assert.True(dialog.IsVisible, "the dialog must stay open on a failed save");

            dialog.Close();
        });

        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void AnUnreadableSettingsFileShowsItsMessageAndTheWindowStaysOpen()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Unix permissions don't reliably block a same-user read the way FileShare.None does on Windows.");

        var path = SettingsFile.Path;
        const string original = """{"mcp_enabled":false,"mcp_port":5152}""";
        File.WriteAllText(path, original);

        HeadlessUi.Run(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.FindControl<ListBox>("SectionList")!.SelectedIndex = 3; // Integrations
            Dispatcher.UIThread.RunJobs();

            var detail = (Control)window.FindControl<ContentControl>("DetailPanel")!.Content!;
            var mcpToggle = detail.GetLogicalDescendants().OfType<CheckBox>().First();

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Flip the MCP toggle so SaveIntegrations has something to write — an unchanged
                // Integrations section skips the write entirely and this would prove nothing.
                mcpToggle.IsChecked = mcpToggle.IsChecked != true;
                Dispatcher.UIThread.RunJobs();

                window.FindControl<Button>("SaveButton")!
                      .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                var errorText = window.FindControl<TextBlock>("SaveErrorText")!;
                Assert.True(errorText.IsVisible, "the refusal must be visible somewhere");
                Assert.Contains(path, errorText.Text);
                Assert.True(IsDirty(window), "a save that threw has not saved Integrations");
            }

            CloseWithoutPrompting(window);
        });

        Assert.Equal(original, File.ReadAllText(path));
    }

    private static bool IsDirty(SettingsWindow window) =>
        (bool)typeof(SettingsWindow)
            .GetField("_isDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static void CloseWithoutPrompting(SettingsWindow window)
    {
        typeof(SettingsWindow)
            .GetField("_isDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, false);
        window.Close();
    }
}
