using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MiniIde.Models;

namespace MiniIde.ViewModels;

public enum ProblemGrouping { ByFile, ByCode }

/// <summary>A top-level tree node in the Problems panel — a file (by-file mode) or a diagnostic code
/// (by-code mode). Built wholesale by <c>ProblemsViewModel.RebuildTree</c>; immutable thereafter.</summary>
public sealed class ProblemGroup
{
    public ProblemGroup(string header, IReadOnlyList<ProblemLeaf> children, bool hasError, string sortKey)
    {
        Header = header;
        Children = children;
        HasError = hasError;
        SortKey = sortKey;
    }

    public string Header { get; }
    public IReadOnlyList<ProblemLeaf> Children { get; }
    /// <summary>True when any child is an error — drives errors-before-warnings top-level ordering.</summary>
    public bool HasError { get; }
    /// <summary>Secondary sort key within a severity band (file path in by-file, code id in by-code).</summary>
    public string SortKey { get; }
}

/// <summary>A leaf tree node: one diagnostic occurrence. Carries the source <see cref="ProblemItem"/> for
/// navigation plus a mode-specific <see cref="Display"/> string (the parent already shows the shared
/// dimension, so the leaf shows the complementary one).</summary>
public sealed class ProblemLeaf
{
    private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.FromRgb(244, 71, 71));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.FromRgb(220, 180, 90));

    public ProblemLeaf(ProblemItem item, string display)
    {
        Item = item;
        Display = display;
    }

    public ProblemItem Item { get; }
    public string Display { get; }
    public string Glyph => Item.Severity == ProblemSeverity.Error ? "⛔" : "⚠";
    public IBrush IconColor => Item.Severity == ProblemSeverity.Error ? ErrorBrush : WarningBrush;
}
