using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace PlanViewer.App.Services;

/// <summary>
/// Clipboard access that never throws. The Windows clipboard is a shared resource:
/// clipboard managers, RDP, and Office clipboard sync briefly lock it, and OpenClipboard
/// then fails with CLIPBRD_E_CANT_OPEN. An unguarded await on SetTextAsync inside an
/// async void handler turns that transient failure into an app crash (issue #415),
/// so all clipboard reads and writes go through here.
/// </summary>
internal static class ClipboardHelper
{
    private const int MaxAttempts = 3;
    private const int RetryDelayMs = 100;

    /// <summary>
    /// Writes text to the clipboard, retrying briefly if it is locked by another
    /// process. Returns false instead of throwing when all attempts fail.
    /// </summary>
    public static async Task<bool> TrySetTextAsync(Visual source, string text)
    {
        var clipboard = TopLevel.GetTopLevel(source)?.Clipboard;
        if (clipboard == null)
            return false;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await clipboard.SetTextAsync(text);
                return true;
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelayMs);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads text from the clipboard, retrying briefly if it is locked by another
    /// process. Returns null when no text is available or all attempts fail.
    /// </summary>
    public static async Task<string?> TryGetTextAsync(Visual source)
    {
        var clipboard = TopLevel.GetTopLevel(source)?.Clipboard;
        if (clipboard == null)
            return null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await clipboard.TryGetTextAsync();
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelayMs);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }
}
