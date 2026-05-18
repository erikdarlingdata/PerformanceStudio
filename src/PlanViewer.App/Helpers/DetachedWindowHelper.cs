using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

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
	/// <returns>The created Window instance.</returns>
	public static Window ShowDetached(
		Control content,
		string title,
		WindowIcon? icon,
		Avalonia.Media.IBrush? backgroundBrush,
		Action<Control> onRedock,
		Action<Control>? onClosing = null)
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

		redockBtn.Click += (_, _) =>
		{
			if (redocked) return;
			redocked = true;

			wrapper.Children.Remove(content);
			detachedWindow.Content = null;
			detachedWindow.Close();
			onRedock(content);
		};

		detachedWindow.Closing += (_, _) =>
		{
			if (!redocked)
				onClosing?.Invoke(content);
		};

		detachedWindow.Show();
		return detachedWindow;
	}
}
