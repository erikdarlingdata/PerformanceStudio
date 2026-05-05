using System;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Avalonia.Media;

namespace PlanViewer.App.Services;

public class SqlCompletionData : ICompletionData
{
    public SqlCompletionData(string keyword)
    {
        Text = keyword;
    }

    public string Text { get; }

    public object Content => Text;

    public object? Description => null;

    public IImage? Image => null;

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, Text);
    }
}
