using System.Threading.Tasks;

namespace MiniIde.ViewModels;

/// <summary>A tab that renders a streamed output buffer (a project run or a NuGet restore) in the main
/// file area. It has no backing file — <see cref="TabViewModelBase.FilePath"/> is null, its identity is a
/// <c>run:</c>/<c>nuget:</c> key, and its <see cref="Header"/> is fixed at construction. Save is a no-op.</summary>
public class OutputTabViewModel : TabViewModelBase
{
    public OutputViewModel Output { get; } = new();

    private readonly string _header;
    public override string Header => _header;

    public OutputTabViewModel(string tabId, string header) : base(tabId, filePath: null) => _header = header;

    public void Append(string line) => Output.Append(line);
    public void Clear() => Output.Clear();

    public override Task SaveAsync() => Task.CompletedTask;
}
