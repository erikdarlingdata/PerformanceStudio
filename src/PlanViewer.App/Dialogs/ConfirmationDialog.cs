using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PlanViewer.App.Dialogs;

/// <summary>
/// Modal yes/no confirmation. Shared rather than per-control: this dialog gates
/// executing SQL against a live server, and it is reached from both the query
/// session and a loaded plan file. Two copies would be free to drift, which is
/// exactly how one of those two paths ended up with no confirmation at all.
/// </summary>
public static class ConfirmationDialog
{
    /// <param name="confirmCaption">What the confirming button says. "OK" reads fine for
    /// gating an execution; a destructive confirmation ("Replace") should name the act, so
    /// the button says what clicking it costs.</param>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmCaption = "OK")
    {
        var result = false;

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };

        var okBtn = new Button
        {
            Content = confirmCaption,
            Height = 32,
            // Min rather than fixed: "OK" renders the same, a longer caption ("Replace") isn't clipped.
            MinWidth = 80,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)owner.FindResource("AppButton")!
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Height = 32,
            MinWidth = 80,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)owner.FindResource("AppButton")!
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children = { messageText, buttonPanel }
        };

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            Height = 260,
            MinWidth = 460,
            MinHeight = 260,
            Icon = owner.Icon,
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Content = content,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        okBtn.Click += (_, _) => { result = true; dialog.Close(); };
        cancelBtn.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return result;
    }
}
