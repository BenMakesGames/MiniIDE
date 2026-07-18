using Microsoft.Build.Locator;
using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Exercises the Roslyn navigation queries — Go to Implementation
/// (<see cref="WorkspaceService.FindImplementationsAsync"/>) and Go to Subclasses
/// (<see cref="WorkspaceService.FindSubclassesAsync"/>) — against a real on-disk solution, mirroring
/// <see cref="WorkspaceServiceTests"/>'s fixture harness (xunit + Shouldly, offsets computed from the fixture
/// text via <c>IndexOf</c> so an edit can't silently invalidate the assertions).</summary>
public sealed class WorkspaceNavigationTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string _root = null!;
    private string _sourcePath = null!;
    private WorkspaceService _workspace = null!;

    // One file holds the whole type zoo: an interface + two implementers, an abstract base + an override (for
    // the FindOverrides union), a three-deep class chain and a sub-interface (transitive subclasses), a leaf
    // and an empty interface (empty-result cases), a framework-interface implementer (in-source-only), and a
    // string literal (the no-symbol case).
    private const string Source = """
        using System;

        namespace Lib;

        public interface IFoo
        {
            void Bar();
        }

        public class A : IFoo
        {
            public void Bar() { }
        }

        public class B : IFoo
        {
            public void Bar() { }
        }

        public abstract class Base
        {
            public abstract void Baz();
        }

        public class Derived : Base
        {
            public override void Baz() { }
        }

        public class Animal { }

        public class Dog : Animal { }

        public class Puppy : Dog { }

        public interface IShape { }

        public interface IRound : IShape { }

        public interface IEmpty { }

        public sealed class Loner { }

        public class Res : IDisposable
        {
            public void Dispose() { }
        }

        public class Msg
        {
            public const string Text = "hello";
        }
        """;

    public async ValueTask InitializeAsync()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

        _root = Path.Combine(Path.GetTempPath(), "miniide-nav-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        await File.WriteAllTextAsync(Path.Combine(_root, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        _sourcePath = Path.Combine(_root, "Types.cs");
        await File.WriteAllTextAsync(_sourcePath, Source);

        var sln = Path.Combine(_root, "Lib.slnx");
        await File.WriteAllTextAsync(sln, """
            <Solution>
              <Project Path="Lib.csproj" />
            </Solution>
            """);

        _workspace = new WorkspaceService();
        await _workspace.EnsureLoadedAsync(sln);
    }

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir; best effort */ }
        return ValueTask.CompletedTask;
    }

    // The offset of the first occurrence of `needle` — lands on the identifier's first char when `needle` is the
    // identifier itself (or is preceded by the keyword in the same span).
    private static int OffsetOf(string needle) => Source.IndexOf(needle, StringComparison.Ordinal);

    private static int LineOf(string needle) =>
        Source[..Source.IndexOf(needle, StringComparison.Ordinal)].Count(c => c == '\n') + 1;

    // Every 1-based line number whose text contains `needle` — for asserting against two identically-worded
    // declarations (the two `public void Bar()` implementers) that LineOf can't tell apart.
    private static IReadOnlyList<int> LinesContaining(string needle)
    {
        var lines = Source.Replace("\r\n", "\n").Split('\n');
        var hits = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(needle, StringComparison.Ordinal)) hits.Add(i + 1);
        return hits;
    }

    [Fact]
    public async Task Implementations_OnAnInterfaceType_ListsEveryImplementingClass()
    {
        var results = await _workspace.FindImplementationsAsync(_sourcePath, OffsetOf("IFoo"), Ct);

        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
        results.ShouldContain(h => h.Location.Line == LineOf("public class A : IFoo"));
        results.ShouldContain(h => h.Location.Line == LineOf("public class B : IFoo"));
    }

    [Fact]
    public async Task Implementations_OnAnInterfaceMember_ListsEveryImplementingMember()
    {
        // The FIRST "Bar" is the interface's declaration `void Bar();`.
        var results = await _workspace.FindImplementationsAsync(_sourcePath, OffsetOf("Bar"), Ct);

        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
        // A.Bar and B.Bar are worded identically, so match the SET of their (distinct) lines.
        results.Select(h => h.Location.Line).OrderBy(n => n)
            .ShouldBe(LinesContaining("public void Bar() { }"));
    }

    [Fact]
    public async Task Implementations_OnAnAbstractMember_ListsItsOverrides()
    {
        // Exercises the FindOverridesAsync union: an abstract class member's overrides are NOT covered by
        // FindImplementationsAsync alone.
        var results = await _workspace.FindImplementationsAsync(_sourcePath, OffsetOf("Baz"), Ct);

        results.ShouldNotBeNull();
        results.ShouldHaveSingleItem().Location.Line.ShouldBe(LineOf("public override void Baz() { }"));
    }

    [Fact]
    public async Task Implementations_OnAFrameworkInterface_ListsOnlyTheInSourceImplementer()
    {
        // IDisposable resolves to a metadata symbol; its solution implementers are found, but metadata
        // implementers (System.IO.Stream, …) must contribute no entry — only in-source locations are listed.
        var results = await _workspace.FindImplementationsAsync(_sourcePath, OffsetOf("IDisposable"), Ct);

        results.ShouldNotBeNull();
        results.ShouldHaveSingleItem().Location.Line.ShouldBe(LineOf("public class Res : IDisposable"));
        results.ShouldAllBe(h => Path.GetFullPath(h.Location.File) == Path.GetFullPath(_sourcePath));
    }

    [Fact]
    public async Task Implementations_OnAnInterfaceWithNoImplementers_IsEmptyNotNull()
    {
        var results = await _workspace.FindImplementationsAsync(_sourcePath, OffsetOf("IEmpty"), Ct);

        // A symbol resolved but has no implementers: an EMPTY list, distinct from the null "no symbol here".
        results.ShouldNotBeNull();
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Subclasses_OnAClass_ListsItsDerivedClassesTransitively()
    {
        var results = await _workspace.FindSubclassesAsync(_sourcePath, OffsetOf("Animal"), Ct);

        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
        results.ShouldContain(h => h.Location.Line == LineOf("public class Dog : Animal"));   // direct
        results.ShouldContain(h => h.Location.Line == LineOf("public class Puppy : Dog"));     // transitive
    }

    [Fact]
    public async Task Subclasses_OnAnInterface_ListsItsDerivedInterfaces()
    {
        var results = await _workspace.FindSubclassesAsync(_sourcePath, OffsetOf("IShape"), Ct);

        results.ShouldNotBeNull();
        results.ShouldHaveSingleItem().Location.Line.ShouldBe(LineOf("public interface IRound : IShape"));
    }

    [Fact]
    public async Task Subclasses_OnALeafClass_IsEmptyNotNull()
    {
        var results = await _workspace.FindSubclassesAsync(_sourcePath, OffsetOf("Loner"), Ct);

        results.ShouldNotBeNull();
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task BothQueries_ReturnNull_WhenNoSymbolResolves()
    {
        // A caret inside a string literal binds to nothing — the null case, distinct from an empty list.
        var literalOffset = OffsetOf("hello");

        (await _workspace.FindImplementationsAsync(_sourcePath, literalOffset, Ct)).ShouldBeNull();
        (await _workspace.FindSubclassesAsync(_sourcePath, literalOffset, Ct)).ShouldBeNull();
    }
}
