# AvaloniaEdit gotchas

- `TextEditor.Document` is CLR property, not AvaloniaProperty → XAML `{Binding Document}` silently no-ops. Set imperatively in code-behind.
- Must add `<StyleInclude Source="avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"/>` in `App.axaml` alongside `FluentTheme` — without it, `TextEditor` renders blank (no ControlTemplate).
- `TextView` has no direct `ExtentHeight` / `Viewport` properties. Cast to `Avalonia.Controls.Primitives.ILogicalScrollable` → `Offset.Y`, `Extent.Height`, `Viewport.Height`.
- `TextDocument.Changing` fires *before* the mutation, `Changed` after. Capture viewport state on `Changing` for pre/post comparisons (e.g. tail-follow "was at bottom before insert").
- Append via `Document.Insert(Document.TextLength, s)` — preserves caret/selection. `Document.Text = s` blows both away; only use for full-reset (`Clear`).
- Trim from top of a `TextDocument` via `Document.GetLineByNumber(1)` → `Document.Remove(line.Offset, line.TotalLength)` — `TotalLength` includes the trailing newline, so line boundaries stay clean.
- Stock xshd definitions live on `AvaloniaEdit.Highlighting.HighlightingManager.Instance`. Names are case-exact — `"XML"` (all caps) and `"Json"` (title case) in v12.0.0. `GetDefinition` returns `null` for unknown names; assign result to `editor.SyntaxHighlighting`.
- `editor.SyntaxHighlighting` and a custom `DocumentColorizingTransformer` (added via `TextArea.TextView.LineTransformers`) both write foreground brushes. Don't run both at once on the same editor — clear whichever isn't in use (`editor.SyntaxHighlighting = null` or the transformer's own reset) on every mode switch, otherwise color from the prior mode bleeds through.
- Stock xshd palettes were authored for light backgrounds. On dark theme, `Json.Punctuation` is literally `Black` (braces/brackets/commas vanish); many XML tokens are low-contrast (`Olive`, `Teal`, `DarkMagenta`). Verify visually.
- Override foregrounds by assigning `HighlightingColor.Foreground = new SimpleHighlightingBrush(Color.FromRgb(...))` on entries in `def.NamedHighlightingColors`. `HighlightingManager.Instance.GetDefinition` returns a shared instance — mutations persist globally; apply once per name.
