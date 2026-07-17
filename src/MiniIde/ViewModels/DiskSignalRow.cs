using System.IO;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MiniIde.Models;

namespace MiniIde.ViewModels;

/// <summary>One row in the Disk panel's signal log: a <see cref="DiskChangeSignal"/> rendered for display.
/// Built on the event and immutable thereafter.
///
/// <para>The formatting lives here rather than on the Model deliberately — <see cref="StructuralReason"/> is a
/// path plus a kind, and a preformatted string in a Model is the thing that rots.</para></summary>
public sealed class DiskSignalRow
{
    private static readonly IBrush OverflowBrush = new ImmutableSolidColorBrush(Color.FromRgb(244, 71, 71));
    private static readonly IBrush StructuralBrush = new ImmutableSolidColorBrush(Color.FromRgb(220, 180, 90));
    private static readonly IBrush ContentBrush = new ImmutableSolidColorBrush(Color.FromRgb(128, 128, 128));

    private DiskSignalRow(string time, string paths, string kind, string detail, IBrush kindColor)
    {
        Time = time;
        Paths = paths;
        Kind = kind;
        Detail = detail;
        KindColor = kindColor;
    }

    public string Time { get; }
    public string Paths { get; }
    /// <summary>Which of the three shapes this burst was: content, structural, or overflow.</summary>
    public string Kind { get; }
    /// <summary>For a structural burst, the path and reason that made it so — the panel's single most valuable
    /// column, since "the tree just collapsed, why?" has no other answer. Empty for a plain content burst.</summary>
    public string Detail { get; }
    public IBrush KindColor { get; }

    /// <param name="root">The watched root, used to shorten paths for display. Null (or a path outside it)
    /// falls back to the full path rather than guessing.</param>
    public static DiskSignalRow For(DiskChangeSignal signal, string? root)
    {
        var (kind, color) = signal switch
        {
            // Overflow first: an overflow signal's paths mean nothing, so that is the headline even if the
            // burst also carried a structural change.
            { Overflow: true } => ("overflow", OverflowBrush),
            { Structural: true } => ("structural", StructuralBrush),
            _ => ("content", ContentBrush),
        };

        var detail = signal.Reason is { } reason
            ? $"{Describe(reason.Kind)} {Relative(reason.Path, root)}"
            : signal.Overflow ? "OS dropped events — paths untrustworthy, rescanning" : "";

        return new DiskSignalRow(
            signal.RaisedUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
            Plural.Of(signal.Paths.Count, "path"),
            kind,
            detail,
            color);
    }

    private static string Describe(StructuralKind kind) => kind switch
    {
        StructuralKind.Created => "created",
        StructuralKind.Deleted => "deleted",
        StructuralKind.Renamed => "renamed",
        StructuralKind.ProjectChanged => "project file changed:",
        _ => "changed",
    };

    internal static string Relative(string path, string? root)
    {
        if (root is null) return path;
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", System.StringComparison.Ordinal) ? path : relative;
    }
}
