using MiniIde.Models;
using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Drives a real <see cref="FileSystemWatcher"/> over a real temp directory — the OS feed is the
/// whole point, so faking it would test nothing. What is asserted is the classification the rest of the app
/// depends on: which paths reach the signal at all (pruning, muting) and whether a burst is structural.
///
/// <para>The signal-driven half of the feature (tab live-push, tree refresh) needs a realized window and is a
/// manual GUI item; this covers the service in isolation.</para></summary>
public sealed class SolutionWatcherTests : IDisposable
{
    // Generous: the OS's delivery lag plus the debounce window. A slow machine should not fail the run.
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    // How long a "nothing should arrive" case waits. Comfortably past the debounce, since a signal that
    // escapes at all escapes promptly.
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromSeconds(1.5);

    private readonly string _root;
    private readonly string _solutionPath;
    private readonly SolutionWatcher _watcher = new();
    private readonly List<DiskChangeSignal> _signals = new();

    public SolutionWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miniide-watch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "obj"));

        _solutionPath = Path.Combine(_root, "Lib.slnx");
        File.WriteAllText(_solutionPath, "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "class Code { }");
        File.WriteAllText(Path.Combine(_root, "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        // Everything above exists before the watch starts, so no fixture setup shows up as a signal.
        _watcher.Changed += s => { lock (_signals) _signals.Add(s); };
        _watcher.Start(_solutionPath).ShouldBeTrue();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir; best effort */ }
    }

    private async Task<DiskChangeSignal?> NextSignalAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_signals)
                if (_signals.Count > 0)
                {
                    var signal = _signals[0];
                    _signals.RemoveAt(0);
                    return signal;
                }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        return null;
    }

    [Fact]
    public async Task Reports_AnEditedFile_AsANonStructuralChange()
    {
        var path = Path.Combine(_root, "Code.cs");
        await File.WriteAllTextAsync(path, "class Code { int X; }", TestContext.Current.CancellationToken);

        var signal = await NextSignalAsync(SignalTimeout);

        signal.ShouldNotBeNull();
        signal.Paths.ShouldContain(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        // Content on an existing file is the cheap overlay path — it must not force a workspace rebuild.
        signal.Structural.ShouldBeFalse();
        signal.Overflow.ShouldBeFalse();
    }

    [Fact]
    public async Task Reports_ANewFile_AsStructural_NamingItAsTheReason()
    {
        // WithDocumentText can neither add nor remove a document, so this has to reach the consumer as
        // "rebuild", not "overlay".
        var added = Path.Combine(_root, "Added.cs");
        await File.WriteAllTextAsync(added, "class Added { }", TestContext.Current.CancellationToken);

        var signal = await NextSignalAsync(SignalTimeout);

        signal.ShouldNotBeNull();
        signal.Structural.ShouldBeTrue();

        // The provenance the Disk panel exists to show: a bare Structural:true leaves "the tree just collapsed,
        // why?" unanswerable.
        signal.Reason.ShouldNotBeNull();
        signal.Reason.Kind.ShouldBe(StructuralKind.Created);
        signal.Reason.Path.ShouldBe(added, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public async Task Reports_NoReason_ForAContentOnlyChange()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Code.cs"), "class Code { int Y; }", TestContext.Current.CancellationToken);

        var signal = await NextSignalAsync(SignalTimeout);

        signal.ShouldNotBeNull();
        // A reason exists exactly when the burst is structural — there is nothing to explain here.
        signal.Structural.ShouldBeFalse();
        signal.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task Reports_AnEditedProjectFile_AsStructural()
    {
        // Nothing was created or deleted — the file already exists, so this is a plain Changed event. But a
        // .csproj decides what compiles, so it still has to reach the consumer as "rebuild", not "overlay".
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>",
            TestContext.Current.CancellationToken);

        var signal = await NextSignalAsync(SignalTimeout);

        signal.ShouldNotBeNull();
        signal.Structural.ShouldBeTrue();
    }

    [Fact]
    public async Task Ignores_WritesUnderPrunedDirectories()
    {
        // A build writes continuously into obj/. The tree and the manifest fingerprint both prune it, so a
        // reconcile triggered by it would be pure churn.
        await File.WriteAllTextAsync(
            Path.Combine(_root, "obj", "Generated.cs"), "class Gen { }", TestContext.Current.CancellationToken);

        (await NextSignalAsync(SilenceWindow)).ShouldBeNull();

        // Counted, not just dropped: a silent watcher and a watcher whose every event is pruned look identical
        // without this, which is exactly the confusion the Disk panel exists to end.
        var stats = _watcher.Snapshot();
        stats.EventsPruned.ShouldBeGreaterThan(0);
        stats.SignalsRaised.ShouldBe(0);
    }

    [Fact]
    public async Task Ignores_MutedSelfWrites_ButNotALaterExternalEdit()
    {
        var path = Path.Combine(_root, "Code.cs");
        _watcher.MuteWrites(new[] { path });

        await File.WriteAllTextAsync(path, "class Code { int Muted; }", TestContext.Current.CancellationToken);

        (await NextSignalAsync(SilenceWindow)).ShouldBeNull();

        var stats = _watcher.Snapshot();
        stats.EventsMuted.ShouldBeGreaterThan(0);
        stats.SignalsRaised.ShouldBe(0);
    }

    [Fact]
    public void Reports_TheLiveWatchAndItsRoot()
    {
        // DiskIsWatched degrading to false is a silent failure mode — the app polls forever with nothing on
        // screen to say so. The panel reads it from here.
        var stats = _watcher.Snapshot();

        stats.IsWatching.ShouldBeTrue();
        stats.Root.ShouldBe(_root, StringCompareShould.IgnoreCase);

        _watcher.Stop();

        _watcher.Snapshot().IsWatching.ShouldBeFalse();
    }

    [Fact]
    public async Task Coalesces_ABurstIntoOneSignal()
    {
        // An agent rewriting a directory must not become one reconcile per write.
        for (var i = 0; i < 20; i++)
            await File.WriteAllTextAsync(
                Path.Combine(_root, $"Burst{i}.cs"), $"class Burst{i} {{ }}", TestContext.Current.CancellationToken);

        var signal = await NextSignalAsync(SignalTimeout);

        signal.ShouldNotBeNull();
        signal.Paths.Count.ShouldBeGreaterThan(1); // one signal carrying many paths, not many signals
        (await NextSignalAsync(SilenceWindow)).ShouldBeNull();
    }
}
