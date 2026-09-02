using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace PlanViewer.App.Helpers;

/// <summary>
/// Reports every change to what a <see cref="TabControl"/> is showing, so state derived from its
/// contents can be recomputed without a call planted at each site that changes them (#447).
///
/// <para><b>Why a collection subscription is not enough.</b> A tab's content is changed two ways,
/// and only one of them is a collection change. Tabs are added and removed, which
/// <c>Items</c> raises; but a tab is also created holding a progress spinner and later has its
/// <c>Content</c> swapped for the finished article — which is how every plan produced by executing
/// a query arrives, and which the collection says nothing about. #449 watched the collection alone
/// and so fixed the file path while leaving the execution path exactly as broken as it was
/// reported.</para>
///
/// <para>Both are watched here, which is the point: the next path that produces a plan is correct
/// without its author knowing this exists, because a plan cannot reach the screen without either
/// adding a tab or filling one in.</para>
/// </summary>
internal static class TabContentWatcher
{
    /// <summary>
    /// Invokes <paramref name="onChanged"/> whenever a tab is added to or removed from
    /// <paramref name="tabs"/>, or an existing tab's content is replaced.
    ///
    /// <para>Meant to be called once, where the control is built. The subscriptions live as long as
    /// the tab control does, which for both call sites is the lifetime of the window.</para>
    /// </summary>
    internal static void Watch(TabControl tabs, Action onChanged)
    {
        /* Tracked rather than derived from the collection-changed args, because a Reset carries
           neither OldItems nor NewItems and would otherwise leave stale subscriptions behind. */
        var watched = new HashSet<TabItem>();

        void OnTabPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ContentControl.ContentProperty)
                onChanged();
        }

        void Resync()
        {
            var current = tabs.Items.OfType<TabItem>().ToHashSet();

            foreach (var gone in watched.Except(current).ToList())
            {
                gone.PropertyChanged -= OnTabPropertyChanged;
                watched.Remove(gone);
            }

            foreach (var arrived in current.Except(watched).ToList())
            {
                arrived.PropertyChanged += OnTabPropertyChanged;
                watched.Add(arrived);
            }
        }

        /* Tabs declared in XAML are already in the collection before anyone gets to watch it. */
        Resync();

        if (tabs.Items is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += (_, _) =>
            {
                Resync();
                onChanged();
            };
        }
    }
}
