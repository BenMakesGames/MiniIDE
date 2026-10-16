using System;

namespace MiniIde.Models;

public enum ProblemSeverity { Error, Warning }

/// <summary>
/// A single compiler diagnostic surfaced in the Problems panel. Plain data handed from
/// <c>WorkspaceService.GetDiagnosticsAsync</c> to the VM — Roslyn types stay inside the service.
/// <see cref="Location"/> is null for locationless diagnostics (e.g. a missing assembly reference);
/// such items are not navigable. A null <see cref="Location"/> is the <em>only</em> encoding of that —
/// there is no "file is null but line/column are 0" sentinel to keep in sync.
/// </summary>
public record ProblemItem(string Id, ProblemSeverity Severity, string Message, SourceLocation? Location)
{
    /// <summary>The diagnostic in MSBuild's own one-line shape — <c>path(line,col): error CS0103: message</c> — so a
    /// copy pasted into chat reads exactly like the build output a coworker would see. <paramref name="displayPath"/>
    /// maps the absolute file path to whatever should be shown (typically solution-relative, which is
    /// meaningful on another machine where the absolute one isn't).</summary>
    public string ToCompilerText(Func<string, string> displayPath)
    {
        var severity = Severity switch
        {
            ProblemSeverity.Error => "error",
            ProblemSeverity.Warning => "warning",
            _ => throw new ArgumentOutOfRangeException(nameof(Severity), Severity, null)
        };
        var body = $"{severity} {Id}: {Message}";
        return Location is null ? body : $"{displayPath(Location.File)}({Location.Line},{Location.Column}): {body}";
    }
}
