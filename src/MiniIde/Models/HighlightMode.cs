namespace MiniIde.Models;

// Syntax-highlight modes dispatched by RefreshAndRedraw (MainWindow.axaml.cs). The extension→mode
// mapping lives in FileKindInfo.Highlight (FileKind.cs); this enum is just the dispatch vocabulary.
public enum HighlightMode { CSharp, Xml, Json, None }
