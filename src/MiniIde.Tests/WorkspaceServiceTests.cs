using Microsoft.Build.Locator;
using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Guards the invariants of reconciling the Roslyn snapshot against the authoritative disk (the IDE
/// is a read-only window; external tools own the writes — see README's "no hand-typed edits" law). Each case
/// simulates the external tool by editing files on disk directly, then reconciling.
///
/// <para>The load-bearing one is <see cref="Reconcile_NeverWritesToDisk"/>: the obvious overlay implementation
/// (<c>MSBuildWorkspace.TryApplyChanges</c>) <em>persists text to the file</em>. Under a disk → view law that
/// would write back to the very disk it's meant to only read. If someone "simplifies"
/// <c>ReconcileWithDiskAsync</c> into using it, that test is what fails.</para></summary>
public sealed class WorkspaceServiceTests : IAsyncLifetime
{
    // A minimal but real solution on disk: MSBuildWorkspace needs to actually evaluate and restore something.
    // Two source files, because the pending-set cases turn on one document being drained while another is not.
    private sealed record Fixture(string Root, string SolutionPath, string SourcePath, string OtherPath);

    // Per-test cancellation: a wedged MSBuild load should fail the test, not hang the run.
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Fixture _fx = null!;
    private WorkspaceService _workspace = null!;

    public async ValueTask InitializeAsync()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

        var root = Path.Combine(Path.GetTempPath(), "miniide-ws-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var csproj = Path.Combine(root, "Lib.csproj");
        await File.WriteAllTextAsync(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        // Target() is on line 5; Caller() calls it on line 6. Offsets below are computed from the text, not
        // hard-coded, so an edit to this fixture can't silently invalidate the assertions.
        var source = Path.Combine(root, "Code.cs");
        await File.WriteAllTextAsync(source, Original);

        var other = Path.Combine(root, "Other.cs");
        await File.WriteAllTextAsync(other, OtherOriginal);

        var sln = Path.Combine(root, "Lib.slnx");
        await File.WriteAllTextAsync(sln, """
            <Solution>
              <Project Path="Lib.csproj" />
            </Solution>
            """);

        _fx = new Fixture(root, sln, source, other);
        _workspace = new WorkspaceService();
        await _workspace.EnsureLoadedAsync(sln);
    }

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        try { Directory.Delete(_fx.Root, recursive: true); } catch { /* temp dir; best effort */ }
        return ValueTask.CompletedTask;
    }

    private const string Original = """
        namespace Lib;

        public static class Code
        {
            public static int Target() => 42;
            public static int Caller() => Target();
        }
        """;

    // Same file, but with a line inserted ABOVE the definitions — this is what shifts every offset below it,
    // and it is precisely the case where resolving against a stale snapshot returns the wrong symbol.
    private const string Edited = """
        namespace Lib;

        // a comment an external tool just wrote, shifting everything below it down a line
        public static class Code
        {
            public static int Target() => 42;
            public static int Caller() => Target();
        }
        """;

    // Byte-for-byte the same length as Original — the two method lines are simply swapped. That makes it the
    // one edit a (LastWriteTimeUtc, Length) stamp cannot see once the mtime is restored, which is exactly the
    // deliberate limitation Reconcile_SkipsAFileWhoseStampIsUnchanged pins down.
    private const string Swapped = """
        namespace Lib;

        public static class Code
        {
            public static int Caller() => Target();
            public static int Target() => 42;
        }
        """;

    // A second document calling into Code.Target(), so a reference to it can be located per-file: the line it
    // reports is how a test observes whether *this* document was reconciled.
    private const string OtherOriginal = """
        namespace Lib;

        public static class Other
        {
            public static int Ping() => Code.Target();
        }
        """;

    private const string OtherEdited = """
        namespace Lib;

        // a comment an external tool just wrote, shifting the call site below it down a line
        public static class Other
        {
            public static int Ping() => Code.Target();
        }
        """;

    private static int OffsetOfDeclaration(string text) =>
        text.IndexOf("Target() => 42", StringComparison.Ordinal);

    // The line Other.cs's call to Target() currently resolves to, per the snapshot.
    private async Task<int> OtherCallSiteLineAsync()
    {
        var references = await _workspace.FindReferencesAsync(_fx.SourcePath, OffsetOfDeclaration(Original), Ct);
        references.ShouldNotBeNull();
        return references.Single(r => Path.GetFullPath(r.Location.File) == Path.GetFullPath(_fx.OtherPath))
            .Location.Line;
    }

    private static int OffsetOfCallSite(string text) =>
        text.IndexOf("Target();", StringComparison.Ordinal);

    private static int LineOf(string text, string needle) =>
        text[..text.IndexOf(needle, StringComparison.Ordinal)].Count(c => c == '\n') + 1;

    [Fact]
    public async Task GoToDefinition_ResolvesAgainstTheOnDiskTextWhenNothingIsDirty()
    {
        var definition = await _workspace.GoToDefinitionAsync(_fx.SourcePath, OffsetOfCallSite(Original), Ct);

        definition.ShouldNotBeNull();
        definition.File.ShouldBe(_fx.SourcePath);
        definition.Line.ShouldBe(LineOf(Original, "public static int Target()"));
    }

    [Fact]
    public async Task GoToDefinition_ResolvesAgainstAFreshExternalDiskEdit()
    {
        // An external tool inserted a new line at the top. The definition has moved down one line, and the
        // caret offset the editor reports is an offset into the EDITED text now on disk.
        await File.WriteAllTextAsync(_fx.SourcePath, Edited, Ct);
        await _workspace.ReconcileWithDiskAsync(Ct);

        var definition = await _workspace.GoToDefinitionAsync(_fx.SourcePath, OffsetOfCallSite(Edited), Ct);

        definition.ShouldNotBeNull();
        definition.Line.ShouldBe(LineOf(Edited, "public static int Target()"));

        // ...and that is genuinely a different line from the pre-edit answer — i.e. this test would pass
        // vacuously if the reconcile did nothing.
        LineOf(Edited, "public static int Target()")
            .ShouldNotBe(LineOf(Original, "public static int Target()"));
    }

    [Fact]
    public async Task Reconcile_NeverWritesToDisk()
    {
        // The external tool already wrote the edit; the reconcile only reflects it inward.
        await File.WriteAllTextAsync(_fx.SourcePath, Edited, Ct);
        var before = await File.ReadAllTextAsync(_fx.SourcePath, Ct);
        var stampBefore = File.GetLastWriteTimeUtc(_fx.SourcePath);

        await _workspace.ReconcileWithDiskAsync(Ct);

        // MSBuildWorkspace.TryApplyChanges would have written the snapshot back to the file here. Under the
        // read-only law reconciliation is strictly disk → view: forking the immutable snapshot must not touch
        // the filesystem at all.
        (await File.ReadAllTextAsync(_fx.SourcePath, Ct)).ShouldBe(before);
        File.GetLastWriteTimeUtc(_fx.SourcePath).ShouldBe(stampBefore);
    }

    [Fact]
    public async Task Reconcile_IsANoOpForTextThatAlreadyMatches()
    {
        // Nothing changed on disk, so the snapshot already matches. Reconciling twice must fork nothing (a
        // needless WithDocumentText would invalidate the cached compilation, turning warm queries cold) and
        // leave the results identical.
        await _workspace.ReconcileWithDiskAsync(Ct);
        await _workspace.ReconcileWithDiskAsync(Ct);

        var definition = await _workspace.GoToDefinitionAsync(_fx.SourcePath, OffsetOfCallSite(Original), Ct);

        definition.ShouldNotBeNull();
        definition.Line.ShouldBe(LineOf(Original, "public static int Target()"));
    }

    [Fact]
    public async Task Reconcile_IgnoresFilesWithNoRoslynDocument()
    {
        var readme = Path.Combine(_fx.Root, "README.md");
        await File.WriteAllTextAsync(readme, "# not a document Roslyn knows about", Ct);

        // A .md is not a Roslyn document and not a .cs, so the disk-driven reconcile neither overlays it nor
        // treats it as structural drift — it is ignored without throwing.
        await Should.NotThrowAsync(() => _workspace.ReconcileWithDiskAsync(Ct));
    }

    [Fact]
    public async Task Reconcile_PicksUpAnExternallyAddedFile()
    {
        // Structural drift: an external tool added a whole new source file to the project. WithDocumentText
        // cannot add a document, so the reconcile must notice and rebuild — after which the new file's call
        // site to Target() is a genuine reference.
        var added = Path.Combine(_fx.Root, "More.cs");
        await File.WriteAllTextAsync(added, """
            namespace Lib;

            public static class More
            {
                public static int Use() => Code.Target();
            }
            """, Ct);

        await _workspace.ReconcileWithDiskAsync(Ct);

        var declarationOffset = Original.IndexOf("Target() => 42", StringComparison.Ordinal);
        var references = await _workspace.FindReferencesAsync(_fx.SourcePath, declarationOffset, Ct);

        references.ShouldNotBeNull();
        references.ShouldContain(r => Path.GetFullPath(r.Location.File) == Path.GetFullPath(added));

        // An MSBuildWorkspace teardown + rebuild is the most expensive thing the reconcile does, so it gets
        // its own number rather than being inferred from the mode counts.
        _workspace.Stats().StructuralReloads.ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_WhenNothingChanged_StatsWithoutReading()
    {
        // The stamp gate asserted directly on the mechanism, rather than inferred from "the answer didn't
        // change". This is the whole thesis of the reconcile: idle focus costs stats, not reads.
        await _workspace.ReconcileWithDiskAsync(Ct); // the first pass reads and stamps every document
        var before = _workspace.Stats();

        await _workspace.ReconcileWithDiskAsync(Ct);

        var after = _workspace.Stats();
        after.DocumentsStatted.ShouldBeGreaterThan(before.DocumentsStatted);
        after.DocumentsRead.ShouldBe(before.DocumentsRead);
    }

    [Fact]
    public async Task Reconcile_WhenContentIsRewrittenIdentically_ReadsWithoutForking()
    {
        await _workspace.ReconcileWithDiskAsync(Ct);
        var before = _workspace.Stats();

        // Same bytes, new mtime — an operation-write that meant nothing. The stamp earns it a read; content is
        // what decides, so nothing forks. (A fork would bust the cached compilation, turning a warm query cold.)
        await File.WriteAllTextAsync(_fx.SourcePath, Original, Ct);

        await _workspace.ReconcileWithDiskAsync(Ct);

        var after = _workspace.Stats();
        after.DocumentsRead.ShouldBe(before.DocumentsRead + 1);
        after.DocumentsForked.ShouldBe(before.DocumentsForked);
    }

    [Fact]
    public async Task Reconcile_KeepsTheFunnelInternallyConsistent()
    {
        // A mixed sequence over both modes: cold-start rescan, a pending-set drain, a forced full rescan, and
        // a drain of an empty set.
        _workspace.DiskIsWatched = true;
        await _workspace.ReconcileWithDiskAsync(Ct);

        await File.WriteAllTextAsync(_fx.SourcePath, Edited, Ct);
        _workspace.MarkPathsChanged(new[] { _fx.SourcePath });
        await _workspace.ReconcileWithDiskAsync(Ct);

        await File.WriteAllTextAsync(_fx.OtherPath, OtherEdited, Ct);
        _workspace.RequestFullRescan();
        await _workspace.ReconcileWithDiskAsync(Ct);

        await _workspace.ReconcileWithDiskAsync(Ct);

        var stats = _workspace.Stats();
        stats.DocumentsRead.ShouldBeLessThanOrEqualTo(stats.DocumentsStatted);
        stats.DocumentsForked.ShouldBeLessThanOrEqualTo(stats.DocumentsRead);
        stats.DocumentsForked.ShouldBeGreaterThan(0); // i.e. the invariant didn't hold vacuously at zero
        stats.Drains.ShouldBe(2);
        stats.FullRescans.ShouldBe(2);
    }

    [Fact]
    public async Task Reconcile_SkipsAFileWhoseStampIsUnchanged()
    {
        // Populate the stamp: a document is only stamped alongside the read that produced the snapshot's copy
        // of it, so the first reconcile after a load reads everything.
        await _workspace.ReconcileWithDiskAsync(Ct);
        var stamp = File.GetLastWriteTimeUtc(_fx.SourcePath);
        var before = _workspace.Stats();

        // Rewrite with genuinely different content, then restore the mtime. Swapped is the same byte length,
        // so (LastWriteTimeUtc, Length) is now identical to what was last synced and the stat says "untouched".
        await File.WriteAllTextAsync(_fx.SourcePath, Swapped, Ct);
        File.SetLastWriteTimeUtc(_fx.SourcePath, stamp);

        await _workspace.ReconcileWithDiskAsync(Ct);

        // The mechanism, alongside the outcome below: nothing was read at all.
        _workspace.Stats().DocumentsRead.ShouldBe(before.DocumentsRead);

        // The file was never read, so the snapshot still answers from Original. This asserts the *limitation*
        // as much as the optimization: a content change preserving BOTH mtime AND length is invisible to the
        // fallback poll. In the running app the watcher fires on the write itself regardless of mtime/size, so
        // this double-coincidence is caught in the primary path — see FileStamp's remarks.
        var definition = await _workspace.GoToDefinitionAsync(_fx.SourcePath, OffsetOfCallSite(Original), Ct);

        definition.ShouldNotBeNull();
        definition.Line.ShouldBe(LineOf(Original, "public static int Target()"));
        LineOf(Swapped, "public static int Target()")
            .ShouldNotBe(LineOf(Original, "public static int Target()")); // i.e. not a vacuous pass
    }

    [Fact]
    public async Task Reconcile_LeavesContentUnforkedWhenOnlyTheStampMoved()
    {
        await _workspace.ReconcileWithDiskAsync(Ct);
        var before = _workspace.Stats();

        // The stamp moves (new mtime) but the bytes are identical — an operation-write that touched a file
        // without meaning anything. The stamp earns it a read; content is what decides, so nothing forks.
        await File.WriteAllTextAsync(_fx.SourcePath, Original, Ct);

        await _workspace.ReconcileWithDiskAsync(Ct);

        _workspace.Stats().DocumentsForked.ShouldBe(before.DocumentsForked);

        var definition = await _workspace.GoToDefinitionAsync(_fx.SourcePath, OffsetOfCallSite(Original), Ct);

        definition.ShouldNotBeNull();
        definition.Line.ShouldBe(LineOf(Original, "public static int Target()"));
    }

    [Fact]
    public async Task Reconcile_WhenWatched_OverlaysThePendingSet()
    {
        _workspace.DiskIsWatched = true;
        await _workspace.ReconcileWithDiskAsync(Ct); // drains the cold-start full rescan

        await File.WriteAllTextAsync(_fx.OtherPath, OtherEdited, Ct);
        _workspace.MarkPathsChanged(new[] { _fx.OtherPath });

        await _workspace.ReconcileWithDiskAsync(Ct);

        (await OtherCallSiteLineAsync()).ShouldBe(LineOf(OtherEdited, "Ping()"));
    }

    [Fact]
    public async Task Reconcile_WhenWatched_LeavesUnreportedFilesAloneUntilAFullRescan()
    {
        _workspace.DiskIsWatched = true;
        await _workspace.ReconcileWithDiskAsync(Ct);

        // Other.cs changed but the watcher never told us — the event was dropped, or its buffer overran.
        await File.WriteAllTextAsync(_fx.OtherPath, OtherEdited, Ct);
        _workspace.MarkPathsChanged(new[] { _fx.SourcePath });

        await _workspace.ReconcileWithDiskAsync(Ct);

        // Drained only what was pending: the unreported edit is not reflected. This is the whole point of the
        // optimization — and the whole reason the fallback below cannot be deleted.
        (await OtherCallSiteLineAsync()).ShouldBe(LineOf(OtherOriginal, "Ping()"));

        // ...and the overflow/focus recovery path finds it. RequestFullRescan is what the watcher's Error
        // event and the focus safety-net both call.
        _workspace.RequestFullRescan();
        await _workspace.ReconcileWithDiskAsync(Ct);

        (await OtherCallSiteLineAsync()).ShouldBe(LineOf(OtherEdited, "Ping()"));
    }

    [Fact]
    public async Task Reconcile_WhenUnwatched_StillFullRescansEveryTime()
    {
        // DiskIsWatched stays false: the watcher never started (or couldn't). Nothing marks paths changed, so
        // a pending-set drain would see an empty set forever and the view would freeze. The reconcile must
        // full-rescan instead — the pre-watcher behavior, now stamp-gated.
        await _workspace.ReconcileWithDiskAsync(Ct);
        await File.WriteAllTextAsync(_fx.OtherPath, OtherEdited, Ct);

        await _workspace.ReconcileWithDiskAsync(Ct);

        (await OtherCallSiteLineAsync()).ShouldBe(LineOf(OtherEdited, "Ping()"));
    }

    [Fact]
    public async Task FindReferences_ReturnsNullWhenNoSymbolResolves()
    {
        // A numeric literal binds to nothing. (Note how hard "no symbol" is to hit: a caret on `namespace Lib`
        // resolves the namespace itself — which is why the UI gates these actions on the classification
        // allowlist, not just on "is the caret over a word".)
        var literalOffset = Original.IndexOf("42", StringComparison.Ordinal);

        var references = await _workspace.FindReferencesAsync(_fx.SourcePath, literalOffset, Ct);

        // Null is "no symbol here" — distinct from an empty list, which means "symbol found, zero references".
        references.ShouldBeNull();
    }

    [Fact]
    public async Task FindReferences_FindsTheCallSite()
    {
        var declarationOffset = Original.IndexOf("Target() => 42", StringComparison.Ordinal);

        var references = await _workspace.FindReferencesAsync(_fx.SourcePath, declarationOffset, Ct);

        references.ShouldNotBeNull();
        references.ShouldContain(r => r.Location.Line == LineOf(Original, "public static int Caller()"));
    }
}
