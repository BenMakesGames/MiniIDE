using Microsoft.Build.Locator;
using MiniIde.Models;
using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Exercises the safe-rename compute + apply path against a real on-disk solution (the same MSBuild
/// fixture shape as <see cref="WorkspaceServiceTests"/>). Rename is driven through
/// <see cref="WorkspaceService.ComputeRenameAsync"/> — the only public seam that pairs the fresh-disk
/// <c>Solution</c> with the resolved <c>ISymbol</c> that <see cref="RenameService.ComputeAsync"/> needs — and
/// applied with <see cref="RenameService.ApplyToDisk"/>. Each test builds its own throwaway solution.</summary>
public sealed class RenameServiceTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<string> _roots = new();
    private readonly List<WorkspaceService> _workspaces = new();

    public ValueTask InitializeAsync()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var ws in _workspaces) ws.Dispose();
        foreach (var root in _roots)
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir; best effort */ }
        return ValueTask.CompletedTask;
    }

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string Slnx = """
        <Solution>
          <Project Path="Lib.csproj" />
        </Solution>
        """;

    private async Task<(WorkspaceService Workspace, string Root)> LoadAsync(params (string Name, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "miniide-rename-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);

        await File.WriteAllTextAsync(Path.Combine(root, "Lib.csproj"), Csproj, Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "Lib.slnx"), Slnx, Ct);
        foreach (var (name, content) in files)
            await File.WriteAllTextAsync(Path.Combine(root, name), content, Ct);

        var ws = new WorkspaceService();
        _workspaces.Add(ws);
        await ws.EnsureLoadedAsync(Path.Combine(root, "Lib.slnx"), Ct);
        return (ws, root);
    }

    private const string CodeWithOverload = """
        namespace Lib;

        public static class Code
        {
            public static int Target() => 42;
            public static int Target(int x) => x;
        }
        """;

    private const string CallerFile = """
        namespace Lib;

        public static class Caller
        {
            // Target is referenced from here
            public static int Use() => Code.Target();
            public static int Over() => Code.Target(1);
            public static string Label() => "Target";
        }
        """;

    [Fact]
    public async Task Rename_RewritesEveryReference_ButLeavesComments_Strings_AndOverloadsAlone()
    {
        var (ws, root) = await LoadAsync(("Code.cs", CodeWithOverload), ("Caller.cs", CallerFile));
        var offset = CodeWithOverload.IndexOf("Target() => 42", StringComparison.Ordinal);

        var outcome = await ws.ComputeRenameAsync(Path.Combine(root, "Code.cs"), offset, "Fetch", Ct);

        outcome.Status.ShouldBe(RenameStatus.Success);
        outcome.Move.ShouldBeNull();

        var codePath = Path.GetFullPath(Path.Combine(root, "Code.cs"));
        var callerPath = Path.GetFullPath(Path.Combine(root, "Caller.cs"));
        outcome.ChangedFiles.Select(f => Path.GetFullPath(f.Path))
            .ShouldBe(new[] { codePath, callerPath }, ignoreOrder: true);

        var code = outcome.ChangedFiles.Single(f => Path.GetFullPath(f.Path) == codePath).NewText;
        code.ShouldContain("Fetch() => 42");
        code.ShouldContain("Target(int x) => x");                 // the overload keeps its name

        var caller = outcome.ChangedFiles.Single(f => Path.GetFullPath(f.Path) == callerPath).NewText;
        caller.ShouldContain("Code.Fetch()");                     // the reference is rewritten
        caller.ShouldContain("Code.Target(1)");                   // ...but the overload call is not
        caller.ShouldContain("// Target is referenced from here"); // comment untouched
        caller.ShouldContain("\"Target\"");                       // string literal untouched
    }

    [Fact]
    public async Task Rename_OfATypeMatchingItsFileName_MovesTheFileOnDisk()
    {
        const string widget = """
            namespace Lib;

            public class Widget
            {
                public int Value => 1;
            }
            """;
        var (ws, root) = await LoadAsync(("Widget.cs", widget));
        var offset = widget.IndexOf("Widget", StringComparison.Ordinal); // the identifier in `class Widget`

        var outcome = await ws.ComputeRenameAsync(Path.Combine(root, "Widget.cs"), offset, "Gadget", Ct);

        outcome.Status.ShouldBe(RenameStatus.Success);
        outcome.Move.ShouldNotBeNull();
        Path.GetFullPath(outcome.Move!.OldPath).ShouldBe(Path.GetFullPath(Path.Combine(root, "Widget.cs")));
        Path.GetFullPath(outcome.Move.NewPath).ShouldBe(Path.GetFullPath(Path.Combine(root, "Gadget.cs")));

        RenameService.ApplyToDisk(outcome);

        File.Exists(Path.Combine(root, "Widget.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(root, "Gadget.cs")).ShouldBeTrue();
        (await File.ReadAllTextAsync(Path.Combine(root, "Gadget.cs"), Ct)).ShouldContain("class Gadget");
    }

    [Fact]
    public async Task Rename_OfAFrameworkSymbol_ReportsNotInSolution_WithNoChanges()
    {
        const string usesConsole = """
            using System;

            namespace Lib;

            public static class Code
            {
                public static void Go() => Console.WriteLine("hi");
            }
            """;
        var (ws, root) = await LoadAsync(("Code.cs", usesConsole));
        var offset = usesConsole.IndexOf("Console.WriteLine", StringComparison.Ordinal);

        var outcome = await ws.ComputeRenameAsync(Path.Combine(root, "Code.cs"), offset, "Terminal", Ct);

        outcome.Status.ShouldBe(RenameStatus.NotInSolution);
        outcome.ChangedFiles.ShouldBeEmpty();
        outcome.Move.ShouldBeNull();
    }

    [Fact]
    public async Task Rename_ThatCollidesWithAnExistingMember_IsBlocked_WithNoChanges()
    {
        const string clashing = """
            namespace Lib;

            public class Box
            {
                public int Count;
                public int Total() => Count;
            }
            """;
        var (ws, root) = await LoadAsync(("Box.cs", clashing));
        var offset = clashing.IndexOf("Total()", StringComparison.Ordinal);

        var outcome = await ws.ComputeRenameAsync(Path.Combine(root, "Box.cs"), offset, "Count", Ct);

        outcome.Status.ShouldBe(RenameStatus.Conflicts);
        outcome.Conflicts.ShouldNotBeEmpty();
        outcome.ChangedFiles.ShouldBeEmpty();
    }
}
