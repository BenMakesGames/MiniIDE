# XML + JSON Syntax Highlighting

## Context
**Current behavior**: Only `.cs` files get colorized (Roslyn `Classifier` via `SyntaxHighlightService` + `RoslynColorizer`). Every other file — including `.csproj`, `.slnx`, `.xml`, `.axaml`, `.json` — renders as plain text.

**New behavior**: XML-family files (`.csproj`, `.slnx`, `.xml`, `.axaml`) and JSON files (`.json`) colorize using AvaloniaEdit's bundled `XML` / `Json` xshd definitions. C# highlighting unchanged. Files of any other extension stay plain.

## Prerequisites
None.

## Scope
### In scope
- New `HighlightMode` enum on `MiniIde.Models`.
- `EditorTabViewModel` — expose `HighlightMode` (derived from extension), replaces `IsCSharp`.
- `MainWindow.BindEditor` / `RefreshAndRedraw` — dispatch on mode; set `editor.SyntaxHighlighting` for Xml/Json, keep Roslyn path for CSharp, clear for None.

### Out of scope
- MSBuild-aware highlighting (special-casing `PropertyGroup`, `$(Prop)` refs, `@(Item)` refs). Follow-up ticket if wanted.
- Custom xshd for any language. Stock definitions only.
- Language auto-detection by content (extension is source of truth).

## Relevant Docs & Anchors
- **Code anchors**:
  - `Services/SyntaxHighlightService.cs` — Roslyn classifier (untouched).
  - `Views/MainWindow.axaml.cs` — `BindEditor`, `RefreshAndRedraw`, `RoslynColorizer`.
  - `ViewModels/EditorTabViewModel.cs` — `IsCSharp` extension check.
- **Docs**: `docs/avaloniaedit.md` — AvaloniaEdit gotchas.
- **Verified in source** (via a scratch enumerator against `Avalonia.AvaloniaEdit` 12.0.0): `HighlightingManager.Instance` exposes definitions named `"XML"` and `"Json"` (case as shown).

## Constraints & Gotchas
- `RoslynColorizer` is attached as a `LineTransformer` on every editor for its lifetime. It's harmless when `_spans` is empty. Keep it attached — do not remove/re-add per file. For Xml/Json/None, ensure `Clear()` is called so it contributes nothing.
- `editor.SyntaxHighlighting` and the `RoslynColorizer` line-transformer both write foreground brushes. They only overlap if both are active simultaneously — the mode dispatch below ensures at most one is contributing color per file.
- Stock xshd color palettes were authored for light backgrounds. The app runs on a dark Fluent theme (`#1E1E22`-ish surfaces). Colors may be legible-but-ugly or, in the worst case, hard to read. Verify visually on both `XML` and `Json`; if unreadable, note as follow-up (do not tune xshd in this ticket unless a color is genuinely unreadable — see Open Decisions).
- `.axaml` counts as XML by extension. Two exist in-repo (`App.axaml`, `Views/MainWindow.axaml`) — they'll start colorizing.

## Open Decisions
1. **Enum location** — `Models/HighlightMode.cs` (new file) vs. nested in `EditorTabViewModel`. Default: separate file under `Models/` (matches `ProjectKind` precedent).
2. **Extension → mode mapping location** — static method on `HighlightMode` (e.g., `HighlightModeExtensions.FromExtension`) vs. inline in `EditorTabViewModel` constructor. Default: static helper alongside the enum — reusable, one obvious home.
3. **Case sensitivity of extension check** — mirror existing `.Equals(".cs", StringComparison.OrdinalIgnoreCase)`. Default: yes, ordinal-ignore-case throughout.
4. **Xshd readability tuning** — if stock colors clash badly on dark bg, defer to follow-up ticket vs. inline tiny palette override now. Default: defer unless a color is unreadable.

## Acceptance Criteria
- [ ] `HighlightMode` enum exists with values `CSharp`, `Xml`, `Json`, `None`.
- [ ] `EditorTabViewModel` exposes a `HighlightMode Mode` (or equivalent) property; `IsCSharp` is removed (no callers left).
- [ ] Extension mapping: `.cs` → `CSharp`; `.csproj`, `.slnx`, `.xml`, `.axaml` → `Xml`; `.json` → `Json`; everything else → `None`. Case-insensitive.
- [ ] Opening a `.csproj` or `.slnx` file shows XML colorization (tag names, attribute names, attribute values, comments visually distinct).
- [ ] Opening a `.json` file shows JSON colorization (keys vs. values, strings vs. numbers visually distinct).
- [ ] Opening a `.cs` file still shows Roslyn colorization exactly as before (keywords, types, strings, comments per the existing `BrushFor` palette).
- [ ] Opening a plain-text file (e.g., `README.md`, an extensionless file) renders with no colorization and no exceptions.
- [ ] Switching tabs between `.cs`, `.csproj`, `.json`, and plain text repeatedly leaves each colored correctly for its mode — no residual color from a prior tab bleeding into the next.

## Implementation

### 1. Add `HighlightMode` enum
New file `Models/HighlightMode.cs`: enum with `CSharp`, `Xml`, `Json`, `None`. In the same file (per Open Decision #2 default), a static helper `HighlightModeExtensions.FromExtension(string extension)` returning the mode for a given file extension. Case-insensitive, ordinal.

### 2. Replace `IsCSharp` with `Mode`
`EditorTabViewModel`: drop the `IsCSharp` property. Add `public HighlightMode Mode { get; }` set in the constructor from `HighlightModeExtensions.FromExtension(Path.GetExtension(filePath))`.

### 3. Dispatch highlighting in `BindEditor`
`MainWindow.axaml.cs` `BindEditor` / `RefreshAndRedraw`: change the `bool isCSharp` parameter to `HighlightMode mode`. Behavior:
- `CSharp`: clear `editor.SyntaxHighlighting`; run the existing Roslyn refresh + redraw path.
- `Xml`: set `editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("XML")`; call `colorizer.Clear()`; redraw.
- `Json`: same as `Xml` but `GetDefinition("Json")`.
- `None`: clear both — `editor.SyntaxHighlighting = null` and `colorizer.Clear()`; redraw.

Also update the `TextChanged` handler wired in `BindEditor` — it passes `tab.IsCSharp` today; replace with `tab.Mode` (only C# actually needs re-classification on edits; Xml/Json xshd tracks text automatically via AvaloniaEdit's internal machinery, no manual refresh needed).

### 4. Sanity-check the `RoslynColorizer` no-op path
Confirm `RoslynColorizer.ColorizeLine` with empty `_spans` costs nothing (early loop exit). It already does. No change needed — noted so the reviewer doesn't wonder.

### 5. Visual pass on stock xshd against dark theme
Run the app, open `MiniIde.csproj`, `MiniIde.slnx`, and (create if needed) a scratch `.json` file. Verify text is legible on the dark background. If any color is unreadable, either (a) tune inline via a small palette override on the returned `IHighlightingDefinition` — see AvaloniaEdit `HighlightingManager` + `HighlightingColor.Foreground` — or (b) file a follow-up ticket. Default: (b), per Open Decision #4.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds; no new warnings.
- [ ] Launch via `scripts/run.ps1`.
- [ ] Open `MiniIde.csproj` — element names, attribute names, attribute values, `<!-- comments -->` visually distinct.
- [ ] Open `MiniIde.slnx` — same XML colorization applied.
- [ ] Open `App.axaml` — XML colorization applied (regression check that AvaloniaEdit isn't confused by XAML-specific bits — worst case is "same as plain XML", which is fine).
- [ ] Create a scratch `test.json` file with `{"key": "value", "num": 42, "arr": [true, null]}` and open it — keys, strings, numbers, `true`/`null` colored.
- [ ] Open a `.cs` file (e.g., `Program.cs`) — Roslyn colorization unchanged (keywords blue, strings orange, types teal, per existing `BrushFor` palette).
- [ ] Open a plain-text file with no extension or an unknown extension (e.g., rename a temp file to `notes.txt`) — no colorization, no exceptions in output pane.
- [ ] Rapidly cycle through open tabs `.cs` → `.csproj` → `.json` → `.cs` several times — each tab's colorization matches its type; no bleed-over.
- [ ] Edit a `.csproj` (e.g., add a whitespace character) — xshd tracks the edit; colorization stays consistent.
- [ ] Edit a `.cs` file — Roslyn re-classifies as before (existing debounce/refresh behavior preserved).
