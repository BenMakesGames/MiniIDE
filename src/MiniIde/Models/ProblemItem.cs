namespace MiniIde.Models;

public enum ProblemSeverity { Error, Warning }

/// <summary>
/// A single compiler diagnostic surfaced in the Problems panel. Plain data handed from
/// <c>WorkspaceService.GetDiagnosticsAsync</c> to the VM — Roslyn types stay inside the service.
/// <see cref="Location"/> is null for locationless diagnostics (e.g. a missing assembly reference);
/// such items are not navigable. A null <see cref="Location"/> is the <em>only</em> encoding of that —
/// there is no "file is null but line/column are 0" sentinel to keep in sync.
/// </summary>
public record ProblemItem(string Id, ProblemSeverity Severity, string Message, SourceLocation? Location);
