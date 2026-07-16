using Avalonia.Threading;
using AvaloniaEdit.Document;

namespace MiniIde.ViewModels;

/// <summary>A tab that renders a streamed output buffer (a project run or a NuGet restore) in the main
/// file area. It has no backing file — <see cref="TabViewModelBase.FilePath"/> is null, its identity is a
/// <c>run:</c>/<c>nuget:</c> key, and its <see cref="Header"/> is fixed at construction.
/// <see cref="Append"/> is called from the background stdout/stderr readers, so both mutators marshal to
/// the UI thread.</summary>
public class OutputTabViewModel : DocumentTabViewModel
{
    private const int MaxLines = 5000;

    private readonly string _header;
    public override string Header => _header;

    public OutputTabViewModel(string tabId, string header)
        : base(tabId, filePath: null, new TextDocument()) => _header = header;

    public void Clear() => Dispatcher.UIThread.Post(() => Document.Text = "");

    public void Append(string line) => Dispatcher.UIThread.Post(() =>
    {
        Document.Insert(Document.TextLength, line + "\n");
        while (Document.LineCount > MaxLines)
        {
            var first = Document.GetLineByNumber(1);
            Document.Remove(first.Offset, first.TotalLength);
        }
    });
}
