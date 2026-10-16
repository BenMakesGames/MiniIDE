using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using MiniIde.Models;

namespace MiniIde.ViewModels;

/// <summary>A file-backed code tab. Construct via <see cref="TabViewModelBase.CreateForFileAsync"/> — the
/// content is read off the UI thread and handed in, so nothing here blocks on the disk. The editor is
/// read-only (see README's "no hand-typed edits" law): there is no write path, and the only mutation is
/// <see cref="ReloadFromDisk"/> reflecting an external tool's edit back into the view.</summary>
public class EditorTabViewModel : DocumentTabViewModel
{
    public HighlightMode Mode { get; }

    // The stamp of the text currently in Document, or null when we've never read under a stamp (a freshly
    // opened tab). Null means "unknown" and costs one read — never a skipped one. As in WorkspaceService, it
    // is only ever written together with the read that produced the buffer, and sampled before it.
    private FileStamp? _synced;

    internal EditorTabViewModel(string filePath, string content)
        : base(FileId(filePath), filePath, new TextDocument(content))
        => Mode = Path.GetExtension(filePath).ToFileKind().GetInfo().Highlight;

    /// <summary>Brings the buffer up to date with disk. The stamp gates the
    /// read, so revalidating an untouched file is a <c>stat</c> — which is what makes it safe to call on every
    /// tab activation and every window focus.
    ///
    /// <para>Failure records nothing on purpose: a file caught mid-write by another tool reads as an
    /// <see cref="IOException"/>, and the honest response is to retry on the next activation rather than
    /// record a stamp for content we never got. A file that has vanished keeps its last-known text.</para></summary>
    public async Task RevalidateFromDiskAsync()
    {
        if (FilePath is null) return;

        var stamp = FileStamp.For(FilePath);
        if (stamp is null) return;
        if (stamp == _synced) return;

        string text;
        try { text = await File.ReadAllTextAsync(FilePath); }
        catch { return; }

        _synced = stamp;
        ReloadFromDisk(text);
    }

    /// <summary>Reflects a fresh disk read back into the editor, replacing the whole buffer. A no-op when the
    /// content already matches — so an unchanged file neither flickers nor resets scroll/caret. The mutation
    /// is marshalled onto the UI thread because AvaloniaEdit's <see cref="TextDocument"/> is thread-affine;
    /// setting <c>Document.Text</c> trips its <c>TextChanged</c>, which is what re-runs the colorizer. Since
    /// the editor is read-only there is never a user edit to clobber (reconciliation is one-directional,
    /// disk → view).</summary>
    public void ReloadFromDisk(string diskText) => Dispatcher.UIThread.Post(() =>
    {
        if (Document.Text == diskText) return;
        Document.Text = diskText;
    });
}
