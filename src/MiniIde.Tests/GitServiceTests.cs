using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Repository discovery — the one part of the git feature with logic worth pinning down, and the
/// thing the whole top-bar state hangs off. Driven against real temp directories rather than an abstraction,
/// because what is being asserted <em>is</em> the filesystem walk: which ancestor wins, and what counts as a
/// <c>.git</c>.
///
/// <para>The command runner is not covered here — it is a process launch, and the manual plan on the ticket
/// exercises it.</para></summary>
public sealed class GitServiceTests : IDisposable
{
    private readonly string _root;

    public GitServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miniide-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir; best effort */ }
    }

    private string Dir(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void RepositoryInTheStartDirectory_IsFound()
    {
        var start = Dir("solution");
        Directory.CreateDirectory(Path.Combine(start, ".git"));

        GitService.FindRepositoryRoot(start).ShouldBe(start);
    }

    // The case the whole "run in the repository root, not the solution directory" rule exists for:
    // <repo>/src/nested/Thing.slnx must pull the outer repository.
    [Fact]
    public void RepositoryTwoLevelsUp_IsFound_AndTheRootIsTheAncestor()
    {
        var repo = Dir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var start = Dir("repo", "src", "nested");

        GitService.FindRepositoryRoot(start).ShouldBe(repo);
    }

    // Linked worktrees and submodules store a `gitdir:` pointer FILE where a normal clone has a directory.
    // Rejecting those would blank the buttons inside a perfectly valid worktree.
    [Fact]
    public void GitFile_CountsAsARepository_NotOnlyAGitDirectory()
    {
        var start = Dir("worktree");
        File.WriteAllText(Path.Combine(start, ".git"), "gitdir: C:/somewhere/.git/worktrees/wt");

        GitService.FindRepositoryRoot(start).ShouldBe(start);
    }

    // The nearest one wins: a nested repository is its own repository, not its parent's.
    [Fact]
    public void NearestAncestorWins_WhenTwoAreNested()
    {
        var outer = Dir("outer");
        Directory.CreateDirectory(Path.Combine(outer, ".git"));
        var inner = Dir("outer", "inner");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));

        GitService.FindRepositoryRoot(Dir("outer", "inner", "deep")).ShouldBe(inner);
    }

    // Walks to the filesystem root and gives up, rather than looping or throwing. (The temp tree itself has
    // no .git; if a machine's %TEMP% ever sat inside a repository this would be a false pass, not a failure.)
    [Fact]
    public void NoRepositoryAnywhereAbove_ReturnsNull()
    {
        GitService.FindRepositoryRoot(Dir("plain", "deeper")).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoStartDirectory_ReturnsNull(string? start)
    {
        // The "no solution open" call — a null answer, not an exception out of a property refresh.
        GitService.FindRepositoryRoot(start).ShouldBeNull();
    }
}
