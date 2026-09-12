using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Covers the one thing the Run flyout takes on faith from the solution model: that
/// <see cref="SolutionService.Configurations"/> is the list a `-c` argument may legally come from. An unknown
/// configuration is not rejected by `dotnet run` — it silently builds a DEBUG-less binary into a bin folder of
/// that name — so this list being right is the only thing standing between a click and a wrong build.</summary>
public sealed class SolutionConfigurationTests : IDisposable
{
    private readonly List<string> _roots = new();

    public void Dispose()
    {
        foreach (var root in _roots)
            try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>Writes a throwaway solution with one (empty but real) project and returns the .slnx path.</summary>
    private string NewSolution(string solutionBody)
    {
        var root = Path.Combine(Path.GetTempPath(), "miniide-sln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        _roots.Add(root);
        File.WriteAllText(Path.Combine(root, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var solution = Path.Combine(root, "Sample.slnx");
        File.WriteAllText(solution, solutionBody);
        return solution;
    }

    [Fact]
    public async Task SolutionDeclaringNothing_StillOffersDebugAndRelease()
    {
        var service = new SolutionService();
        await service.LoadAsync(NewSolution("""
            <Solution>
              <Project Path="App/App.csproj" />
            </Solution>
            """), TestContext.Current.CancellationToken);

        service.Configurations.ShouldBe(new[] { "Debug", "Release" }, ignoreOrder: true);
    }

    [Fact]
    public async Task CustomConfiguration_Surfaces()
    {
        var service = new SolutionService();
        await service.LoadAsync(NewSolution("""
            <Solution>
              <Configurations>
                <BuildType Name="Debug" />
                <BuildType Name="Release" />
                <BuildType Name="Profiling" />
              </Configurations>
              <Project Path="App/App.csproj" />
            </Solution>
            """), TestContext.Current.CancellationToken);

        service.Configurations.ShouldContain("Profiling");
    }
}
