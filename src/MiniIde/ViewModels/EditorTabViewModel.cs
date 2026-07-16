using System.IO;
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

    internal EditorTabViewModel(string filePath, string content)
        : base(FileId(filePath), filePath, new TextDocument(content))
        => Mode = Path.GetExtension(filePath).ToFileKind().GetInfo().Highlight;

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
