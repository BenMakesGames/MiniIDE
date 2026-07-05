# AvaloniaEdit gotchas

- `TextEditor.Document` is CLR property, not AvaloniaProperty → XAML `{Binding Document}` silently no-ops. Set imperatively in code-behind.
- Must add `<StyleInclude Source="avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"/>` in `App.axaml` alongside `FluentTheme` — without it, `TextEditor` renders blank (no ControlTemplate).
- `TextView` has no direct `ExtentHeight` / `Viewport` properties. Cast to `Avalonia.Controls.Primitives.ILogicalScrollable` → `Offset.Y`, `Extent.Height`, `Viewport.Height`.
- `TextDocument.Changing` fires *before* the mutation, `Changed` after. Capture viewport state on `Changing` for pre/post comparisons (e.g. tail-follow "was at bottom before insert").
- Append via `Document.Insert(Document.TextLength, s)` — preserves caret/selection. `Document.Text = s` blows both away; only use for full-reset (`Clear`).
- Trim from top of a `TextDocument` via `Document.GetLineByNumber(1)` → `Document.Remove(line.Offset, line.TotalLength)` — `TotalLength` includes the trailing newline, so line boundaries stay clean.
