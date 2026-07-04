# AvaloniaEdit gotchas

- `TextEditor.Document` is CLR property, not AvaloniaProperty → XAML `{Binding Document}` silently no-ops. Set imperatively in code-behind.
- Must add `<StyleInclude Source="avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"/>` in `App.axaml` alongside `FluentTheme` — without it, `TextEditor` renders blank (no ControlTemplate).
