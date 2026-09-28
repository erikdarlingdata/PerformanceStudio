using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Database_SelectionChanged calls FetchDatabaseMetadataAsync on every pick, and that fetch used
/// to let whichever call finished last write _serverMetadata.Database — even for a database the
/// user had already clicked past. Picking twice in a row, before the first fetch can land, has to
/// leave only the newest pick's fetch able to write (E7). Same shape, and same proof technique, as
/// <see cref="QueryStoreDatabaseCheckRaceTests"/> — reflect out the CancellationTokenSource each
/// pick is running on rather than waiting for the pretend server's connection attempts to resolve.
/// </summary>
public class DatabaseMetadataFetchRaceTests
{
    [Fact]
    public void PickingASecondDatabaseCancelsTheFirstMetadataFetch()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session, database: "master");

                // FetchDatabaseMetadataAsync's null guard returns before ever reaching the CTS
                // swap without this — a fresh, empty ServerMetadata is enough to get past it.
                SetField(session, "_serverMetadata", new ServerMetadata());

                var databaseBox = session.FindControl<ComboBox>("DatabaseBox")!;
                databaseBox.ItemsSource = new List<string> { "master", "A", "B" };
                databaseBox.SelectedItem = "master";

                databaseBox.SelectedItem = "A";
                var firstFetch = (CancellationTokenSource?)GetField(session, "_databaseMetadataCts");
                Assert.NotNull(firstFetch);
                Assert.False(firstFetch!.IsCancellationRequested);

                databaseBox.SelectedItem = "B";
                var secondFetch = (CancellationTokenSource?)GetField(session, "_databaseMetadataCts");

                Assert.NotSame(firstFetch, secondFetch);
                Assert.True(firstFetch.IsCancellationRequested,
                    "picking a second database left the first one's metadata fetch running alongside it");

                // The superseded fetch reads its own token when it wakes, and Token on a disposed
                // source throws — so the newer pick must cancel the older source and leave it alone.
                var readingItAgain = Record.Exception(() => firstFetch.Token);
                Assert.True(readingItAgain == null,
                    "the newer pick disposed the older fetch's token source, which the older fetch still reads");
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
