using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PlanViewer.App.Dialogs;

/// <summary>
/// What the user said when asked about a query tab with unsaved changes.
/// </summary>
public enum UnsavedChangesChoice
{
    /// <summary>Write the query out, then close.</summary>
    Save,

    /// <summary>Close and lose the edit — an explicit answer, not a default.</summary>
    DontSave,

    /// <summary>Do not close anything.</summary>
    Cancel
}

/// <summary>
/// The Save / Don't Save / Cancel prompt for a modified query tab (#462).
///
/// <para>Separate from <see cref="ConfirmationDialog"/> rather than a parameter on it: this
/// has three answers, and the third one has to be distinguishable from the second. A yes/no
/// dialog collapses "don't save" and "cancel" into the same false, which is the one mistake
/// this prompt cannot make — it is the difference between losing a tab and losing the app.</para>
/// </summary>
public static class UnsavedChangesDialog
{
    /// <summary>
    /// Asks about one tab. Dismissing the window any other way — title-bar close, Escape —
    /// is <see cref="UnsavedChangesChoice.Cancel"/>, because the safe answer to a question
    /// nobody answered is to leave the work where it is.
    /// </summary>
    public static async Task<UnsavedChangesChoice> ShowAsync(Window owner, string tabLabel)
    {
        var choice = UnsavedChangesChoice.Cancel;

        var messageText = new TextBlock
        {
            Text = $"Do you want to save the changes you made to {tabLabel}?\n\nYour changes will be lost if you don't save them.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var dialog = new Window
        {
            Title = "Unsaved Changes",
            Width = 460,
            Height = 220,
            MinWidth = 460,
            MinHeight = 220,
            Icon = owner.Icon,
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        Button MakeButton(string caption, UnsavedChangesChoice answer)
        {
            var button = new Button
            {
                Content = caption,
                Height = 32,
                MinWidth = 96,
                Padding = new Avalonia.Thickness(16, 0),
                FontSize = 12,
                Margin = new Avalonia.Thickness(8, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Theme = (Avalonia.Styling.ControlTheme)owner.FindResource("AppButton")!
            };
            button.Click += (_, _) => { choice = answer; dialog.Close(); };
            buttonPanel.Children.Add(button);
            return button;
        }

        MakeButton("Save", UnsavedChangesChoice.Save);
        MakeButton("Don't Save", UnsavedChangesChoice.DontSave);
        MakeButton("Cancel", UnsavedChangesChoice.Cancel);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children = { messageText, buttonPanel }
        };

        await dialog.ShowDialog(owner);
        return choice;
    }
}
