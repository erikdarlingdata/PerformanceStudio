using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PlanViewer.App.Helpers;

/// <summary>
/// Creates a detached free-floating window that wraps content with a Re-dock toolbar.
/// Consolidates the shared detach pattern used by MainWindow tabs and QuerySession sub-tabs.
/// </summary>
internal static class DetachedWindowHelper
{
	/// <summary>
	/// Creates and shows a detached window for the given content.
	/// </summary>
	/// <param name="content">The control to host in the window.</param>
	/// <param name="title">Window title.</param>
	/// <param name="icon">Optional window icon.</param>
	/// <param name="backgroundBrush">Window background brush.</param>
	/// <param name="onRedock">Called when the user clicks Re-dock. Content has already been removed from the wrapper.</param>
	/// <param name="onClosing">Called when the window is closing (before destroy). Use to cancel fetches etc.</param>
	/// <param name="closeGuard">
	/// Asked, synchronously, whether this close has a question attached to it (#473).
	///
	/// <para>Returning null closes on the spot with nothing asked and nothing delayed — that is
	/// read-only content, an unmodified query, and app shutdown, so the path a plan or a Query
	/// Store window takes is the one it always took. Returning a task cancels the close until
	/// that task answers: true re-issues it, false leaves the window open.</para>
	///
	/// <para>Re-dock never consults it. The content is being moved, not destroyed, so there is
	/// nothing to save it from.</para>
	/// </param>
	/// <returns>The created Window instance.</returns>
	public static Window ShowDetached(
		Control content,
		string title,
		WindowIcon? icon,
		Avalonia.Media.IBrush? backgroundBrush,
		Action<Control> onRedock,
		Action<Control>? onClosing = null,
		Func<Control, Window, Task<bool>?>? closeGuard = null)
	{
		var redockBtn = new Button
		{
			Content = "Re-dock",
			FontSize = 12,
			Padding = new Avalonia.Thickness(8, 4),
			Margin = new Avalonia.Thickness(4),
			Background = Brushes.Transparent,
			Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB)),
			BorderThickness = new Avalonia.Thickness(1),
			BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
			VerticalAlignment = VerticalAlignment.Center
		};

		var toolbar = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Children = { redockBtn }
		};
		DockPanel.SetDock(toolbar, Dock.Top);

		var wrapper = new DockPanel
		{
			Children = { toolbar, content }
		};

		var detachedWindow = new Window
		{
			Title = title,
			Width = 1280,
			Height = 800,
			MinWidth = 900,
			MinHeight = 600,
			WindowStartupLocation = WindowStartupLocation.CenterScreen,
			Background = backgroundBrush ?? Brushes.Black,
			Content = wrapper,
			Icon = icon
		};

		bool redocked = false;

		// Set once the guard has answered, so the re-issued close is not questioned again.
		bool closeConfirmed = false;

		redockBtn.Click += (_, _) =>
		{
			if (redocked) return;
			redocked = true;

			wrapper.Children.Remove(content);
			detachedWindow.Content = null;
			detachedWindow.Close();
			onRedock(content);
		};

		// Avalonia's Closing is synchronous and an unsaved-changes prompt is not, so the first
		// pass cancels the close outright and re-issues it once the question has an answer.
		// Same cancel-then-reissue MainWindow.OnClosing uses (#462); closeConfirmed is the
		// latch that stops the second pass asking all over again.
		async Task ReissueCloseIfConfirmed(Task<bool> pending)
		{
			if (!await pending)
				return;

			closeConfirmed = true;

			// Posted rather than called: a guard that answers without ever actually waiting
			// would otherwise land Close() in the middle of the Closing handler that called
			// it. Safe to post because the guard returns null during app shutdown, so nothing
			// is ever queued against a dispatcher that is going away.
			Dispatcher.UIThread.Post(detachedWindow.Close);
		}

		detachedWindow.Closing += (_, e) =>
		{
			if (redocked)
				return;

			if (!closeConfirmed && closeGuard != null)
			{
				var pending = closeGuard(content, detachedWindow);
				if (pending != null)
				{
					e.Cancel = true;
					_ = ReissueCloseIfConfirmed(pending);
					return;
				}
			}

			onClosing?.Invoke(content);
		};

		detachedWindow.Show();
		return detachedWindow;
	}
}
