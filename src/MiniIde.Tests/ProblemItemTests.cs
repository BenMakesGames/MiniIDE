using MiniIde.Models;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>The copy-to-clipboard text: MSBuild's diagnostic shape, so a pasted problem reads like build output.</summary>
public sealed class ProblemItemTests
{
    [Fact]
    public void Located_problem_uses_the_display_path_and_msbuild_shape()
    {
        var item = new ProblemItem("CS0103", ProblemSeverity.Error, "The name 'x' does not exist in the current context",
            new SourceLocation(@"C:\repo\src\Foo.cs", 12, 5));

        item.ToCompilerText(_ => @"src\Foo.cs")
            .ShouldBe(@"src\Foo.cs(12,5): error CS0103: The name 'x' does not exist in the current context");
    }

    [Fact]
    public void Locationless_problem_omits_the_path_prefix()
    {
        var item = new ProblemItem("CS0006", ProblemSeverity.Warning, "Metadata file not found", null);

        item.ToCompilerText(_ => throw new InvalidOperationException("no path to map"))
            .ShouldBe("warning CS0006: Metadata file not found");
    }
}
