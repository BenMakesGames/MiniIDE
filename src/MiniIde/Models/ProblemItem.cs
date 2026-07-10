namespace MiniIde.Models;

public enum ProblemSeverity { Error, Warning }

/// <summary>
/// A single compiler diagnostic surfaced in the Problems panel. Plain data handed from
/// <c>WorkspaceService.GetDiagnosticsAsync</c> to the VM — Roslyn types stay inside the service.
/// <see cref="File"/> is null for locationless diagnostics (e.g. a missing assembly reference); such
/// items carry no navigable position (<see cref="Line"/>/<see cref="Column"/> are 0).
/// </summary>
public record ProblemItem(string Id, ProblemSeverity Severity, string Message, string? File, int Line, int Column)
{
    public bool HasLocation => !string.IsNullOrEmpty(File);
}
