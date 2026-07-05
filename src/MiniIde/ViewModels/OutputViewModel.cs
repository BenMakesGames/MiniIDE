using Avalonia.Threading;
using AvaloniaEdit.Document;

namespace MiniIde.ViewModels;

public class OutputViewModel : ViewModelBase
{
    public TextDocument Document { get; } = new();

    public void Clear() => Dispatcher.UIThread.Post(() => Document.Text = "");

    public void Append(string line) => Dispatcher.UIThread.Post(() =>
    {
        Document.Insert(Document.TextLength, line + "\n");
        while (Document.LineCount > 5000)
        {
            var first = Document.GetLineByNumber(1);
            Document.Remove(first.Offset, first.TotalLength);
        }
    });
}
