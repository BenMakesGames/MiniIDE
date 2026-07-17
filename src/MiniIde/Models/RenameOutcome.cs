using System.Collections.Generic;

namespace MiniIde.Models;

/// <summary>How a rename attempt resolved. Plain data handed from the rename compute path to the VM — Roslyn
/// types stay inside the services.</summary>
public enum RenameStatus
{
    /// <summary>Every reference was rewritten; <see cref="RenameOutcome.ChangedFiles"/> is ready to apply.</summary>
    Success,
    /// <summary>Nothing resolved at the caret — no symbol to rename.</summary>
    NoSymbol,
    /// <summary>The symbol resolves to metadata (a framework / NuGet type), not this solution's source, so it
    /// cannot be safely renamed here.</summary>
    NotInSolution,
    /// <summary>The new name would collide with an existing member; blocked, nothing written
    /// (see <see cref="RenameOutcome.Conflicts"/>).</summary>
    Conflicts,
}

/// <summary>One file the rename rewrites: its <paramref name="Path"/> and the full <paramref name="NewText"/>
/// to write there. For the type-matched file that is also being moved, <paramref name="Path"/> is the
/// <em>new</em> path — the move happens first, then this text lands at the new path.</summary>
public record RenamedFile(string Path, string NewText);

/// <summary>A file rename that rides along with a type rename (<c>Foo</c> in <c>Foo.cs</c> → <c>Bar.cs</c>).</summary>
public record RenameFileMove(string OldPath, string NewPath);

/// <summary>The result of computing a solution-wide rename against the fresh-disk snapshot. Only
/// <see cref="Status"/> == <see cref="RenameStatus.Success"/> carries files to write; the other statuses carry
/// an explanation for the status bar and leave the disk untouched.</summary>
public record RenameOutcome(
    RenameStatus Status,
    IReadOnlyList<RenamedFile> ChangedFiles,
    RenameFileMove? Move,
    IReadOnlyList<string> Conflicts)
{
    private static readonly IReadOnlyList<RenamedFile> NoFiles = new List<RenamedFile>();
    private static readonly IReadOnlyList<string> NoConflicts = new List<string>();

    public static RenameOutcome NoSymbol() => new(RenameStatus.NoSymbol, NoFiles, null, NoConflicts);
    public static RenameOutcome NotInSolution() => new(RenameStatus.NotInSolution, NoFiles, null, NoConflicts);
    public static RenameOutcome Conflicted(IReadOnlyList<string> conflicts) =>
        new(RenameStatus.Conflicts, NoFiles, null, conflicts);
    public static RenameOutcome Renamed(IReadOnlyList<RenamedFile> changed, RenameFileMove? move) =>
        new(RenameStatus.Success, changed, move, NoConflicts);
}

/// <summary>What a symbol at the caret looks like to the rename UI, without leaking a Roslyn <c>ISymbol</c> to
/// the view: its current <paramref name="Name"/> (to pre-fill the prompt) and whether it is
/// <paramref name="InSolution"/> (defined in this solution's source, so renameable at all).</summary>
public record RenameTarget(string Name, bool InSolution);
