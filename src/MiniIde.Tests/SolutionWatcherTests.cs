using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Drives a real <see cref="FileSystemWatcher"/> over a real temp directory — the OS feed is the
/// whole point, so faking it would test nothing. What is asserted is the one decision the watcher makes: which
/// changes can alter the project list (and so raise a signal) and which can't.</summary>
public sealed class SolutionWatcherTests : IDisposable
{
    // Generous: the OS's delivery lag plus the debounce window. A slow machine should not fail the run.
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    // How long a "nothing should arrive" case waits. Comfortably past the debounce, since a signal that
    // escapes at all escapes promptly.
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromSeconds(1.5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root;
    private readonly string _solutionPath;
    private readonly SolutionWatcher _watcher = new();
    private int _signals;

    public SolutionWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miniide-watch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Lib", "Properties"));
        Directory.CreateDirectory(Path.Combine(_root, ".miniide"));

        _solutionPath = Path.Combine(_root, "Lib.slnx");
        File.WriteAllText(_solutionPath, "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Lib", "Code.cs"), "class Code { }");
        File.WriteAllText(Path.Combine(_root, "Lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        // Everything above exists before the watch starts, so no fixture setup shows up as a signal.
        _watcher.Changed += () => Interlocked.Increment(ref _signals);
        _watcher.Start(_solutionPath).ShouldBeTrue();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir; best effort */ }
    }

    private async Task<bool> SignalArrivesWithinAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref _signals) > 0) return true;
            await Task.Delay(25, Ct);
        }
        return false;
    }

    [Theory]
    [InlineData("Lib.slnx")]
    [InlineData("Lib/Lib.csproj")]
    [InlineData("Lib/Properties/launchSettings.json")]
    [InlineData(".miniide/Lib.local.json")]
    public async Task Signals_WhenAFileTheProjectListReadsChanges(string relative)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, relative), "<changed />", Ct);

        (await SignalArrivesWithinAsync(SignalTimeout)).ShouldBeTrue();
    }

    [Fact]
    public async Task Signals_WhenAProjectFileIsRenamed()
    {
        File.Move(Path.Combine(_root, "Lib", "Lib.csproj"), Path.Combine(_root, "Lib", "Renamed.csproj"));

        (await SignalArrivesWithinAsync(SignalTimeout)).ShouldBeTrue();
    }

    [Fact]
    public async Task StaysQuiet_WhenOnlySourceFilesChange()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Lib", "Code.cs"), "class Code { int X; }", Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "Lib", "New.cs"), "class New { }", Ct);

        (await SignalArrivesWithinAsync(SilenceWindow)).ShouldBeFalse();
    }

    [Fact]
    public async Task Coalesces_ABurstIntoOneSignal()
    {
        var csproj = Path.Combine(_root, "Lib", "Lib.csproj");
        for (var i = 0; i < 10; i++)
            await File.WriteAllTextAsync(csproj, $"<Project Sdk=\"Microsoft.NET.Sdk\"><!-- {i} --></Project>", Ct);

        (await SignalArrivesWithinAsync(SignalTimeout)).ShouldBeTrue();
        await Task.Delay(SilenceWindow, Ct);
        Volatile.Read(ref _signals).ShouldBe(1);
    }

    [Theory]
    [InlineData(@"C:\src\App.sln", true)]
    [InlineData(@"C:\src\App.SLNX", true)]
    [InlineData(@"C:\src\App\App.csproj", true)]
    [InlineData(@"C:\src\App\Properties\launchSettings.json", true)]
    [InlineData(@"C:\src\.miniide\App.local.json", true)]
    [InlineData(@"C:\src\.miniide\App.local.json.tmp", false)]
    [InlineData(@"C:\src\.miniide\.gitignore", false)]
    [InlineData(@"C:\src\App\Program.cs", false)]
    [InlineData(@"C:\src\App\appsettings.json", false)]
    [InlineData(@"C:\src\App\obj\App.csproj.nuget.g.props", false)]
    public void IsRelevant_MatchesOnlyWhatTheProjectListReads(string path, bool expected) =>
        SolutionWatcher.IsRelevant(path).ShouldBe(expected);

    [Fact]
    public async Task Snapshot_CountsWhatItSaw_AndLogsTheCause()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Lib", "Code.cs"), "class Code { int X; }", Ct);
        var csproj = Path.Combine(_root, "Lib", "Lib.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><!-- x --></Project>", Ct);

        (await SignalArrivesWithinAsync(SignalTimeout)).ShouldBeTrue();
        var stats = _watcher.Snapshot();

        stats.Root.ShouldBe(_root);
        stats.Reloads.ShouldBe(1);
        stats.EventsRelevant.ShouldBeGreaterThan(0);
        stats.EventsSeen.ShouldBeGreaterThan(stats.EventsRelevant); // the .cs write was seen but filtered
        stats.RecentBursts.ShouldHaveSingleItem().Paths.ShouldBe([csproj]);
    }

    [Fact]
    public void Snapshot_HasNoRoot_AfterStop()
    {
        _watcher.Stop();

        _watcher.Snapshot().Root.ShouldBeNull();
    }
}
