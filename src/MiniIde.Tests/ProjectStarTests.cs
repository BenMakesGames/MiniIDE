using MiniIde.Services;
using MiniIde.ViewModels;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Starring: where it's stored, that it stays out of git, and that it pins a project to the top across
/// a restart. Drives the real window view model over a real solution on disk.</summary>
public sealed class ProjectStarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miniide-stars-" + Guid.NewGuid().ToString("N"));
    private readonly string _solution;
    private readonly List<MainWindowViewModel> _vms = new();

    public ProjectStarTests()
    {
        const string library = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";
        const string exe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>";
        foreach (var (name, body) in new[] { ("App", exe), ("Core", library), ("Tool", exe) })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(Path.Combine(_root, name, name + ".csproj"), body);
        }
        _solution = Path.Combine(_root, "Sample.slnx");
        File.WriteAllText(_solution, """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Core/Core.csproj" />
              <Project Path="Tool/Tool.csproj" />
            </Solution>
            """);
    }

    public void Dispose()
    {
        foreach (var vm in _vms) vm.Watcher.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<MainWindowViewModel> OpenAsync()
    {
        var vm = new MainWindowViewModel();
        _vms.Add(vm);
        await vm.OpenSolutionAsync(_solution);
        return vm;
    }

    private static string[] Order(MainWindowViewModel vm) => vm.Projects.Select(p => p.Name).ToArray();

    private static ProjectItemViewModel Card(MainWindowViewModel vm, string name) => vm.Projects.Single(p => p.Name == name);

    [Fact]
    public async Task Starring_PinsTheProjectToTheTop_EvenAboveRunnableOnes()
    {
        var vm = await OpenAsync();
        Order(vm).ShouldBe(["App", "Tool", "Core"]);

        Card(vm, "Core").ToggleStarCommand.Execute(null);

        Order(vm).ShouldBe(["Core", "App", "Tool"]);
        Card(vm, "Core").IsStarred.ShouldBeTrue();
    }

    [Fact]
    public async Task Stars_SurviveARestart()
    {
        var first = await OpenAsync();
        Card(first, "Tool").ToggleStarCommand.Execute(null);

        var second = await OpenAsync();

        Order(second).ShouldBe(["Tool", "App", "Core"]);
    }

    [Fact]
    public async Task Unstarring_ReturnsTheProjectToItsPlace()
    {
        var vm = await OpenAsync();
        Card(vm, "Tool").ToggleStarCommand.Execute(null);
        Card(vm, "Tool").ToggleStarCommand.Execute(null);

        Order(vm).ShouldBe(["App", "Tool", "Core"]);
        LocalSolutionSettings.Load(_solution).StarredProjects.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stars_AreStoredSolutionRelative_InAGitIgnoredLocalFile()
    {
        var vm = await OpenAsync();
        Card(vm, "App").ToggleStarCommand.Execute(null);

        LocalSolutionSettings.PathFor(_solution).ShouldBe(Path.Combine(_root, ".miniide", "Sample.local.json"));
        LocalSolutionSettings.Load(_solution).StarredProjects.ShouldBe([Path.Combine("App", "App.csproj")]);
        (await File.ReadAllTextAsync(Path.Combine(_root, ".miniide", ".gitignore"), TestContext.Current.CancellationToken))
            .ShouldContain("*.local.json");
    }

    [Fact]
    public async Task AnUnreadableSettingsFile_IsNeverOverwritten()
    {
        // A hand-edit with a typo must not cost the user every star the moment they click one.
        var settings = LocalSolutionSettings.PathFor(_solution);
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(settings, "{ not json", TestContext.Current.CancellationToken);

        var vm = await OpenAsync();
        Card(vm, "App").ToggleStarCommand.Execute(null);

        (await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken)).ShouldBe("{ not json");
        vm.Status.ShouldContain("Not saving stars");
    }

    [Fact]
    public void NoFileYet_MeansNoStars() =>
        LocalSolutionSettings.Load(_solution).ShouldBe(LocalSolutionSettings.Empty);
}
