using MiniIde.Models;
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
    // Two source files, so a test can edit the one it isn't navigating from.
    private sealed record Fixture(string Root, string SolutionPath, string ProjectPath, string SourcePath, string OtherPath);

    // Per-test cancellation: a wedged MSBuild load should fail the test, not hang the run.
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Fixture _fx = null!;
    private WorkspaceService _workspace = null!;

    public async ValueTask InitializeAsync()
    {
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

        _fx = new Fixture(root, sln, csproj, source, other);
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

    // A second document calling into Code.Target(). Navigating from its call site is how a test observes whether
    // *this* document was reconciled: against stale text, an offset into the edited text misses the call.
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
        // Nothing changed on disk, so the snapshot already matches. Reconciling twice must leave the results
        // identical.
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

        // A .md is not a Roslyn document and not a .cs, so the reconcile neither overlays it nor treats it as
        // structural drift — it is ignored without throwing.
        await Should.NotThrowAsync(() => _workspace.ReconcileWithDiskAsync(Ct));
    }

    [Fact]
    public async Task Reconcile_PicksUpAnExternallyAddedFile()
    {
        // Structural drift: an external tool added a whole new source file to the project. WithDocumentText
        // cannot add a document, so the reconcile must notice and rebuild — after which the new file is a
        // document whose call to Target() resolves.
        const string more = """
            namespace Lib;

            public static class More
            {
                public static int Use() => Code.Target();
            }
            """;
        var added = Path.Combine(_fx.Root, "More.cs");
        await File.WriteAllTextAsync(added, more, Ct);

        await _workspace.ReconcileWithDiskAsync(Ct);

        var definition = await _workspace.GoToDefinitionAsync(added, OffsetOfCallSite(more), Ct);
        definition.ShouldNotBeNull();
        definition.File.ShouldBe(_fx.SourcePath);
    }

    [Fact]
    public async Task Reconcile_PicksUpAnEditToEveryDocument()
    {
        // No one tells the workspace which file changed: every reconcile is a whole-solution, stamp-gated pass,
        // so an edit to any document is reflected — here, the one that isn't being navigated from.
        await _workspace.ReconcileWithDiskAsync(Ct);
        await File.WriteAllTextAsync(_fx.OtherPath, OtherEdited, Ct);

        await _workspace.ReconcileWithDiskAsync(Ct);

        // The caret offset is into the EDITED text; against a stale snapshot it would land a line too early.
        var definition = await _workspace.GoToDefinitionAsync(_fx.OtherPath, OffsetOfCallSite(OtherEdited), Ct);
        definition.ShouldNotBeNull();
        definition.Line.ShouldBe(LineOf(Original, "public static int Target()"));
    }

    [Fact]
    public async Task Reconcile_SkipsAFileWhoseStampIsUnchanged()
    {
        // Populate the stamp: a document is only stamped alongside the read that produced the snapshot's copy
        // of it, so the first reconcile after a load reads everything.
        await _workspace.ReconcileWithDiskAsync(Ct);
        var stamp = File.GetLastWriteTimeUtc(_fx.SourcePath);

        // Rewrite with genuinely different content, then restore the mtime. Swapped is the same byte length,
        // so (LastWriteTimeUtc, Length) is now identical to what was last synced and the stat says "untouched".
        await File.WriteAllTextAsync(_fx.SourcePath, Swapped, Ct);
        File.SetLastWriteTimeUtc(_fx.SourcePath, stamp);

        await _workspace.ReconcileWithDiskAsync(Ct);

        // The file was never read, so the snapshot still answers from Original. This asserts the *limitation*
        // as much as the optimization: a content change preserving BOTH mtime AND length is invisible to the
        // reconcile — see FileStamp's remarks.
        var definition = await _workspace.GoToDefinitionAsync(_fx.SourcePath, OffsetOfCallSite(Original), Ct);

        definition.ShouldNotBeNull();
        definition.Line.ShouldBe(LineOf(Original, "public static int Target()"));
        LineOf(Swapped, "public static int Target()")
            .ShouldNotBe(LineOf(Original, "public static int Target()")); // i.e. not a vacuous pass
    }

    [Fact]
    public async Task GetDiagnostics_ReportsTheRequestedProjectsErrors()
    {
        await File.WriteAllTextAsync(_fx.SourcePath, Original.Replace("=> 42;", "=> \"not an int\";"), Ct);
        await _workspace.ReconcileWithDiskAsync(Ct);

        var problems = await _workspace.GetDiagnosticsAsync(_fx.ProjectPath, Ct);

        problems.ShouldContain(p => p.Severity == ProblemSeverity.Error
                                    && p.Location != null
                                    && Path.GetFullPath(p.Location.File) == Path.GetFullPath(_fx.SourcePath));
    }

    [Fact]
    public async Task GetDiagnostics_IsEmptyForAProjectOutsideTheSolution()
    {
        await File.WriteAllTextAsync(_fx.SourcePath, Original.Replace("=> 42;", "=> \"not an int\";"), Ct);
        await _workspace.ReconcileWithDiskAsync(Ct);

        var problems = await _workspace.GetDiagnosticsAsync(Path.Combine(_fx.Root, "Elsewhere.csproj"), Ct);

        problems.ShouldBeEmpty();
    }
}
