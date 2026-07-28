using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PlanViewer.App.Services;

/// <summary>
/// App-wide interception of TextBox clipboard operations. Avalonia's built-in
/// TextBox.Copy()/Cut() are async void with an unguarded clipboard await, and
/// Paste() catches only TimeoutException, so a locked Windows clipboard
/// (CLIPBRD_E_CANT_OPEN) crashes the app (issue #415). TextBox raises these
/// routed events before touching the clipboard and skips its internal path when
/// they are handled, so one class handler per event covers every TextBox in the
/// app, including ones created later.
/// </summary>
internal static class TextBoxClipboardGuard
{
    public static void Register()
    {
        TextBox.CopyingToClipboardEvent.AddClassHandler<TextBox>(OnCopying);
        TextBox.CuttingToClipboardEvent.AddClassHandler<TextBox>(OnCutting);
        TextBox.PastingFromClipboardEvent.AddClassHandler<TextBox>(OnPasting);
    }

    // CanCopy/CanCut/CanPaste mirror the built-in gates (password box, read-only,
    // empty selection); when they are false the built-in path is a no-op that never
    // touches the clipboard, so leaving the event unhandled there is safe.

    private static void OnCopying(TextBox textBox, RoutedEventArgs e)
    {
        if (!textBox.CanCopy)
            return;

        e.Handled = true;
        _ = ClipboardHelper.TrySetTextAsync(textBox, textBox.SelectedText);
    }

    private static async void OnCutting(TextBox textBox, RoutedEventArgs e)
    {
        if (!textBox.CanCut)
            return;

        e.Handled = true;
        // Handling the event suppresses the built-in DeleteSelection, so clear the
        // selection here - and only once the text actually reached the clipboard.
        if (await ClipboardHelper.TrySetTextAsync(textBox, textBox.SelectedText))
            textBox.SelectedText = "";
    }

    private static async void OnPasting(TextBox textBox, RoutedEventArgs e)
    {
        if (!textBox.CanPaste)
            return;

        e.Handled = true;
        var text = await ClipboardHelper.TryGetTextAsync(textBox);
        if (string.IsNullOrEmpty(text))
            return;

        if (!textBox.AcceptsReturn)
            text = text.Replace("\r", "").Replace("\n", "");

        textBox.SelectedText = text;
    }
}
