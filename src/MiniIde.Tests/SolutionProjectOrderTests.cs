using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>The project list's order is the left panel's order: runnable projects first, then the rest, each
/// group by name.</summary>
public sealed class SolutionProjectOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miniide-order-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Project(string name, string body)
    {
        Directory.CreateDirectory(Path.Combine(_root, name));
        File.WriteAllText(Path.Combine(_root, name, name + ".csproj"), body);
    }

    [Fact]
    public async Task RunnableProjectsComeFirst_ThenTheRest_EachByName()
    {
        const string library = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";
        const string exe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>";
        Project("Alpha", library);
        Project("Zeta", exe);
        Project("Beta", library);
        Project("Gamma", exe);
        var solution = Path.Combine(_root, "Sample.slnx");
        await File.WriteAllTextAsync(solution, """
            <Solution>
              <Project Path="Alpha/Alpha.csproj" />
              <Project Path="Beta/Beta.csproj" />
              <Project Path="Gamma/Gamma.csproj" />
              <Project Path="Zeta/Zeta.csproj" />
            </Solution>
            """, TestContext.Current.CancellationToken);

        var service = new SolutionService();
        await service.LoadAsync(solution, TestContext.Current.CancellationToken);

        service.Projects.Select(p => p.Name).ShouldBe(["Gamma", "Zeta", "Alpha", "Beta"]);
    }
}
