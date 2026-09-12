using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Covers the two things the Run flyout depends on the reader getting right: only profiles
/// <c>dotnet run -lp</c> honours are offered (every other <c>commandName</c> is accepted-then-ignored by the
/// CLI, so an unfiltered list would offer entries that silently do nothing), and file order is preserved so
/// the first entry is the one <c>dotnet run</c> would have picked unprompted.</summary>
public sealed class LaunchProfileReaderTests : IDisposable
{
    private readonly List<string> _roots = new();

    public void Dispose()
    {
        foreach (var root in _roots)
            try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>Writes a throwaway project directory and returns the project path. A null
    /// <paramref name="launchSettings"/> / <paramref name="runJson"/> means "don't create that file".</summary>
    private string NewProject(string? launchSettings = null, string? runJson = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "miniide-lp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        var project = Path.Combine(root, "Sample.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        if (launchSettings is not null)
        {
            Directory.CreateDirectory(Path.Combine(root, "Properties"));
            File.WriteAllText(Path.Combine(root, "Properties", "launchSettings.json"), launchSettings);
        }
        if (runJson is not null) File.WriteAllText(Path.Combine(root, "Sample.run.json"), runJson);
        return project;
    }

    [Fact]
    public void NoLaunchSettings_YieldsNoProfiles() =>
        LaunchProfileReader.Read(NewProject()).ShouldBeEmpty();

    [Fact]
    public void KeepsOnlyProjectProfiles_InFileOrder()
    {
        var project = NewProject(launchSettings: """
            {
              "profiles": {
                "IIS Express": { "commandName": "IISExpress" },
                "http":        { "commandName": "Project" },
                "Run script":  { "commandName": "Executable" },
                "https":       { "commandName": "Project" }
              }
            }
            """);

        LaunchProfileReader.Read(project).ShouldBe(new[] { "http", "https" });
    }

    [Fact]
    public void ReadsRunJson_OnlyWhenLaunchSettingsIsAbsent()
    {
        var onlyRunJson = NewProject(runJson: """
            { "profiles": { "fromRunJson": { "commandName": "Project" } } }
            """);
        LaunchProfileReader.Read(onlyRunJson).ShouldBe(new[] { "fromRunJson" });

        // dotnet's own precedence: the established location wins outright, and the file-based-app file is not
        // even consulted — so the two must never be merged.
        var both = NewProject(
            launchSettings: """{ "profiles": { "fromLaunchSettings": { "commandName": "Project" } } }""",
            runJson: """{ "profiles": { "fromRunJson": { "commandName": "Project" } } }""");
        LaunchProfileReader.Read(both).ShouldBe(new[] { "fromLaunchSettings" });
    }

    [Fact]
    public void MalformedOrShapelessFile_YieldsNoProfiles()
    {
        LaunchProfileReader.Read(NewProject(launchSettings: "{ not json at all")).ShouldBeEmpty();
        LaunchProfileReader.Read(NewProject(launchSettings: """{ "profiles": "nope" }""")).ShouldBeEmpty();
        // A profile with no commandName is not a Project profile, so it is not offered.
        LaunchProfileReader.Read(NewProject(launchSettings: """{ "profiles": { "bare": {} } }""")).ShouldBeEmpty();
    }
}
