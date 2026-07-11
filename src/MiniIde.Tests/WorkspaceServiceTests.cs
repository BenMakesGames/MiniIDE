using Microsoft.Build.Locator;
using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Guards the invariants of overlaying unsaved editor buffers onto the Roslyn snapshot.
///
/// <para>The load-bearing one is <see cref="SyncDocuments_NeverWritesToDisk"/>: the obvious implementation
/// (<c>MSBuildWorkspace.TryApplyChanges</c>) <em>persists the new text to the file</em>, which would silently
/// autosave the user's buffer. If someone "simplifies" <c>SyncDocumentsAsync</c> into using it, that test is
/// what fails.</para></summary>
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
    // and it is precisely the case where resolving against the on-disk text returns the wrong symbol.
    private const string Edited = """
        namespace Lib;

        // a comment the user just typed, shifting everything below it down a line
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
    public async Task GoToDefinition_ResolvesAgainstTheUnsavedBuffer_NotTheFileOnDisk()
    {
        // The user typed a new line at the top but hasn't saved. The definition has moved down one line, and
        // the caret offset the editor reports is an offset into the EDITED text.
        await _workspace.SyncDocumentsAsync([(_fx.SourcePath, Edited)], Ct);

        var definition = await _workspace.GoToDefinitionAsync(_fx.SourcePath, OffsetOfCallSite(Edited), Ct);

        definition.ShouldNotBeNull();
        definition.Line.ShouldBe(LineOf(Edited, "public static int Target()"));

        // ...and that is genuinely a different line from the on-disk answer — i.e. this test would pass
        // vacuously if the overlay did nothing.
        LineOf(Edited, "public static int Target()")
            .ShouldNotBe(LineOf(Original, "public static int Target()"));
    }

    [Fact]
    public async Task SyncDocuments_NeverWritesToDisk()
    {
        var before = await File.ReadAllTextAsync(_fx.SourcePath, Ct);
        var stampBefore = File.GetLastWriteTimeUtc(_fx.SourcePath);

        await _workspace.SyncDocumentsAsync([(_fx.SourcePath, Edited)], Ct);

        // MSBuildWorkspace.TryApplyChanges would have saved the buffer here, silently autosaving the user's
        // file. Forking the immutable snapshot must not touch the filesystem at all.
        (await File.ReadAllTextAsync(_fx.SourcePath, Ct)).ShouldBe(before);
        File.GetLastWriteTimeUtc(_fx.SourcePath).ShouldBe(stampBefore);
    }

    [Fact]
    public async Task SyncDocuments_IsANoOpForTextThatAlreadyMatches()
    {
        // Re-applying identical text would still invalidate the cached compilation, turning every warm
        // semantic query back into a cold one. Syncing the same text twice must leave the results identical.
        await _workspace.SyncDocumentsAsync([(_fx.SourcePath, Original)], Ct);
        await _workspace.SyncDocumentsAsync([(_fx.SourcePath, Original)], Ct);

        var definition = await _workspace.GoToDefinitionAsync(_fx.SourcePath, OffsetOfCallSite(Original), Ct);

        definition.ShouldNotBeNull();
        definition.Line.ShouldBe(LineOf(Original, "public static int Target()"));
    }

    [Fact]
    public async Task SyncDocuments_IgnoresOpenFilesThatArentPartOfTheSolution()
    {
        var readme = Path.Combine(_fx.Root, "README.md");
        await File.WriteAllTextAsync(readme, "# not a document Roslyn knows about", Ct);

        // Should not throw — an open .md tab is dirty like any other, and simply has no Roslyn document.
        await Should.NotThrowAsync(() => _workspace.SyncDocumentsAsync([(readme, "# edited")], Ct));
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
