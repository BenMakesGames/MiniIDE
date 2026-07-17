using System;
using System.IO;

namespace MiniIde.Models;

/// <summary>A file's cheap identity — the pair a <c>stat</c> yields — used to decide whether reading it is
/// worth the I/O.
///
/// <para><b>This is a pre-filter for whether to read, never the drift decision.</b> The read-only ticket
/// established "content-hash, never mtime" because an operation-write can bump mtime without a meaningful
/// change and a same-size overwrite changes content the length can't reveal. Both still hold: a stamp change
/// only earns the file a read, after which <c>ContentEquals</c> (snapshot) or a text compare (tab) decides
/// whether anything actually changed. So an mtime-bumped-but-identical file still forks nothing.</para>
///
/// <para>The one behavior this relaxes: a content change preserving <b>both</b> mtime <b>and</b> byte length
/// is invisible to the fallback poll. Under normal operation <see cref="Services.SolutionWatcher"/> fires on
/// the write itself regardless of mtime/size, so that double-coincidence is caught in the primary path; the
/// relaxation is confined to cold-start/overflow. <see cref="Length"/> is in the stamp (not mtime alone) so
/// same-size-different-mtime and different-size writes are both caught by a <c>stat</c>.</para></summary>
public readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
{
    /// <summary>The stamp of <paramref name="path"/>, or <c>null</c> when it doesn't exist or can't be
    /// stat'd. A null stamp always means "don't claim to know this file" — callers leave their last-known
    /// content alone rather than treating a vanished/locked file as changed.</summary>
    public static FileStamp? For(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
