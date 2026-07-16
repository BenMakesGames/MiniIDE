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
    private sealed record Fixture(string Root, string SolutionPath, string SourcePath);

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

        var sln = Path.Combine(root, "Lib.slnx");
        await File.WriteAllTextAsync(sln, """
            <Solution>
              <Project Path="Lib.csproj" />
            </Solution>
            """);

        _fx = new Fixture(root, sln, source);
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
