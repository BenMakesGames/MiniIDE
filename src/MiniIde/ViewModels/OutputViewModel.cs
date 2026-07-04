using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace MiniIde.ViewModels;

public class OutputViewModel : ViewModelBase
{
    public ObservableCollection<string> Lines { get; } = new();
    public void Clear() => Dispatcher.UIThread.Post(Lines.Clear);
    public void Append(string line) => Dispatcher.UIThread.Post(() =>
    {
        Lines.Add(line);
        if (Lines.Count > 5000) Lines.RemoveAt(0);
    });
}
