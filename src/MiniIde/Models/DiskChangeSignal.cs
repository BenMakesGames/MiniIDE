using System;
using System.Collections.Generic;

namespace MiniIde.Models;

/// <summary>What about a path made a burst structural. Created/Deleted/Renamed move the *set* of documents;
/// ProjectChanged is a <c>.csproj</c> whose content decides what compiles. All four force a rebuild rather
/// than an overlay, but they are worth telling apart: "the tree just collapsed — why?" is unanswerable from a
/// bare <c>Structural: true</c>.</summary>
public enum StructuralKind { Created, Deleted, Renamed, ProjectChanged }

/// <summary>The first cause that made a burst structural: the path, and what about it. A record rather than a
/// preformatted string — the panel formats, so the Model can't rot into a display concern.</summary>
public record StructuralReason(string Path, StructuralKind Kind);

/// <summary>One debounced burst of disk activity under the solution root, as reported by
/// <see cref="Services.SolutionWatcher"/>. A file touched five times in the window is one entry in
/// <paramref name="Paths"/>, and a whole <c>git checkout</c> is ideally one signal.</summary>
/// <param name="Paths">Full paths that changed. Empty is meaningful when <paramref name="Overflow"/> is set —
/// that is precisely the "something changed but we don't know what" case.</param>
/// <param name="Structural">A file was created, deleted, or renamed, or a project file changed — i.e. the set
/// of documents may have moved, which no text overlay can express. The consumer must rebuild rather than
/// overlay, and refresh the tree.</param>
/// <param name="Overflow">The OS dropped events (<c>FileSystemWatcher</c>'s internal buffer overran under a
/// burst, or it errored). Nothing in this signal can be trusted to be complete; the consumer must rescan.</param>
/// <param name="RaisedUtc">When the burst ended and became a signal — stamped at the debounce's trailing edge,
/// not at the first event, because the burst is the signal's identity.</param>
/// <param name="Reason">Why <paramref name="Structural"/> is set: the <em>first</em> event in the burst that
/// set it, since the first cause is the interesting one. Null exactly when <paramref name="Structural"/> is
/// false. An <paramref name="Overflow"/>-only signal has no reason — no path is known to have caused it.</param>
public record DiskChangeSignal(
    IReadOnlyCollection<string> Paths,
    bool Structural,
    bool Overflow,
    DateTime RaisedUtc,
    StructuralReason? Reason);
