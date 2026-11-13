using System.Collections.Frozen;
using System.Collections.Generic;
using Avalonia.Media;

namespace MiniIde.Models;

public enum ProjectKind { Exe, Lib, Web, Tst }

/// <param name="Glyph">A <see cref="ProjectIcon"/> glyph constant.</param>
/// <param name="Color">A <see cref="ProjectIconPalette"/> brush.</param>
/// <param name="IsRunnable">Whether the project can be selected as a startup/run target.</param>
public record ProjectKindInfo(string Glyph, IBrush Color, bool IsRunnable);

public static class ProjectKindExtensions
{
    // Everything except a class library is runnable; one entry per enum member.
    private static readonly FrozenDictionary<ProjectKind, ProjectKindInfo> Info = new Dictionary<ProjectKind, ProjectKindInfo>
    {
        [ProjectKind.Exe] = new(ProjectIcon.Exe, ProjectIconPalette.Exe, true),
        [ProjectKind.Lib] = new(ProjectIcon.Lib, ProjectIconPalette.Lib, false),
        [ProjectKind.Web] = new(ProjectIcon.Web, ProjectIconPalette.Web, true),
        [ProjectKind.Tst] = new(ProjectIcon.Tst, ProjectIconPalette.Tst, true),
    }.ToFrozenDictionary();

    public static ProjectKindInfo GetInfo(this ProjectKind kind) => Info[kind];
}
