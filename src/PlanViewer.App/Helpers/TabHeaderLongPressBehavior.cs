using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace PlanViewer.App.Helpers;

/// <summary>
/// Attaches long-press-to-detach behavior to a tab header panel.
/// Fires the callback after 500 ms if the pointer stays within a 6 px dead-zone.
/// </summary>
internal static class TabHeaderLongPressBehavior
{
	/// <summary>
	/// Wires PointerPressed / PointerReleased / PointerMoved on <paramref name="header"/>
	/// so that a 500 ms long-press triggers <paramref name="onLongPress"/>.
	/// An optional <paramref name="onMiddleClick"/> callback handles middle-button clicks.
	/// </summary>
	public static void Attach(
		Panel header,
		Action onLongPress,
		Action? onMiddleClick = null)
	{
		DispatcherTimer? longPressTimer = null;
		Point longPressStartPoint = default;

		header.PointerPressed += (_, e) =>
		{
			if (onMiddleClick != null &&
				e.GetCurrentPoint(null).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
			{
				onMiddleClick();
				e.Handled = true;
				return;
			}

			if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
			{
				longPressStartPoint = e.GetPosition(header);
				longPressTimer?.Stop();
				longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
				longPressTimer.Tick += (_, _) =>
				{
					longPressTimer.Stop();
					longPressTimer = null;
					onLongPress();
				};
				longPressTimer.Start();
			}
		};

		header.PointerReleased += (_, _) =>
		{
			longPressTimer?.Stop();
			longPressTimer = null;
		};

		header.PointerMoved += (_, e) =>
		{
			if (longPressTimer == null) return;
			var pos = e.GetPosition(header);
			var dx = Math.Abs(pos.X - longPressStartPoint.X);
			var dy = Math.Abs(pos.Y - longPressStartPoint.Y);
			if (dx > 6 || dy > 6)
			{
				longPressTimer.Stop();
				longPressTimer = null;
			}
		};
	}
}
