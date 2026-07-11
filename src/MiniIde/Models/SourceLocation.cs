namespace MiniIde.Models;

/// <summary>A navigable position in a source file. <see cref="Line"/> and <see cref="Column"/> are
/// 1-based, matching what an editor displays (Roslyn's <c>LinePosition</c> is 0-based, so services
/// translating from it add 1 at the boundary and nowhere else).
///
/// <para>This is the single currency for "somewhere to jump to" — search hits, reference results,
/// diagnostics, and go-to-definition all speak it, so navigation never has to unpack a positional
/// tuple.</para></summary>
public record SourceLocation(string File, int Line, int Column);
