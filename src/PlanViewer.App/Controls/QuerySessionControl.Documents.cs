using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace PlanViewer.App.Controls;

/* The session's document strip: the plan, Query Store and schema tabs the session opens, said
   once. The Query Editor is the anonymous TabItem the XAML declares at index 0 of SubTabControl
   and it cannot be removed, so "document" here means any tab in the strip that is not the editor.

   THIS FILE IS THE ONLY PLACE ALLOWED TO KNOW THE EDITOR SITS AT INDEX 0. Every other partial
   adds, removes, selects and enumerates documents through the members below and never reaches
   into SubTabControl's items or selection itself, so when the editor stops being a tab in this
   strip, this is the only file that has to change. */
public partial class QuerySessionControl : UserControl
{
    /// <summary>
    /// Every document this session holds, in strip order — the editor excluded.
    /// </summary>
    private IEnumerable<TabItem> DocumentTabs => SubTabControl.Items.OfType<TabItem>().Skip(1);

    /// <summary>
    /// Whether the session holds any document at all. The editor on its own is not one.
    /// </summary>
    private bool HasDocuments => SubTabControl.Items.Count > 1;

    /// <summary>
    /// The document the user is looking at, or null when that is the editor — or when the strip
    /// has no selection at all, which is what removing the selected tab leaves behind until the
    /// TabControl picks the next one.
    /// </summary>
    private TabItem? SelectedDocument =>
        SubTabControl.SelectedIndex == 0 ? null : SubTabControl.SelectedItem as TabItem;

    /// <summary>
    /// Whether the editor, rather than one of the documents, is the selected tab.
    /// </summary>
    private bool IsEditorSelected => SubTabControl.SelectedIndex == 0;

    /// <summary>Puts a new document at the end of the strip. It does not become the selected one.</summary>
    private void AddDocument(TabItem tab) => SubTabControl.Items.Add(tab);

    /// <summary>Takes a document out of the strip.</summary>
    private void RemoveDocument(TabItem tab) => SubTabControl.Items.Remove(tab);

    /// <summary>Brings a document to the front.</summary>
    private void SelectDocument(TabItem tab) => SubTabControl.SelectedItem = tab;

    /// <summary>Goes back to the editor.</summary>
    private void SelectEditor() => SubTabControl.SelectedIndex = 0;
}
