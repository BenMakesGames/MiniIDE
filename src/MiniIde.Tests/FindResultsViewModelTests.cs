using MiniIde.Models;
using MiniIde.Services;
using MiniIde.ViewModels;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>The Find panel's synchronous state transitions — no search runs here. Covers the context-banner
/// mode a symbol query puts the panel in (<see cref="FindResultsViewModel.ShowResults"/>), the close command
/// that reverts it, and the no-symbol outcome that stays in normal mode. The VM constructs cheaply in
/// isolation (its collaborators are plain services and a no-op open callback), so none of this touches disk or
/// Roslyn.</summary>
public sealed class FindResultsViewModelTests
{
    private static FindResultsViewModel NewVm() =>
        new(new SearchService(), new SolutionService(), _ => Task.CompletedTask);

    private static FindHit Hit(string file = "A.cs") => new(new SourceLocation(file, 1, 1), "preview");

    [Fact]
    public void ShowResults_EntersContextMode_WithLabelAndStatus()
    {
        var vm = NewVm();

        vm.ShowResults([Hit("A.cs"), Hit("B.cs")], "implementation", "Implementations of \"IFoo\"");

        vm.IsContextResult.ShouldBeTrue();
        vm.ContextLabel.ShouldBe("Implementations of \"IFoo\"");
        vm.Status.ShouldBe("2 implementations");
        vm.Results.Count.ShouldBe(2);
    }

    [Fact]
    public void ShowResults_ZeroHits_StillShowsBanner()
    {
        var vm = NewVm();

        vm.ShowResults([], "implementation", "Implementations of \"IFoo\"");

        vm.IsContextResult.ShouldBeTrue();
        vm.Status.ShouldBe("0 implementations");
        vm.Results.ShouldBeEmpty();
    }

    [Fact]
    public void CloseContext_ClearsInputsAndBanner_ButKeepsResults()
    {
        var vm = NewVm();
        vm.ShowResults([Hit()], "reference", "Usages of \"Foo\"");
        // Simulate a user who had checkboxes toggled and a query typed before the symbol query.
        vm.Query = "leftover";
        vm.UseRegex = true;
        vm.CaseSensitive = true;

        vm.CloseContextCommand.Execute(null);

        vm.IsContextResult.ShouldBeFalse();
        vm.ContextLabel.ShouldBe("");
        vm.Query.ShouldBe("");
        vm.UseRegex.ShouldBeFalse();
        vm.CaseSensitive.ShouldBeFalse();
        // The results the banner described are left in place to browse.
        vm.Results.Count.ShouldBe(1);
    }

    [Fact]
    public void ShowNoResults_StaysInNormalMode()
    {
        var vm = NewVm();
        // Put it in context mode first: ShowNoResults must revert even from a prior banner.
        vm.ShowResults([Hit()], "reference", "Usages of \"Foo\"");

        vm.ShowNoResults("No symbol found");

        vm.IsContextResult.ShouldBeFalse();
        vm.ContextLabel.ShouldBe("");
        vm.Status.ShouldBe("No symbol found");
        vm.Results.ShouldBeEmpty();
    }
}
