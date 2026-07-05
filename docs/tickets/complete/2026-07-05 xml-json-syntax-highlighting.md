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

## Learnings

- **Enum + helper co-located** (Open Decision #1, #2 → both defaults taken): `HighlightMode` enum and `HighlightModeExtensions.FromExtension` live in same file `Models/HighlightMode.cs`. Matches `ProjectKind` precedent for enum-per-file; the tiny helper doesn't warrant its own file.
- **Case-insensitive throughout** (Open Decision #3 → default): `FromExtension` uses `StringComparison.OrdinalIgnoreCase` for every extension check, mirroring the previous `IsCSharp` idiom.
- **Dispatch is a `switch` in `RefreshAndRedraw`, not the caller**: caller (`BindEditor`) still calls `RefreshAndRedraw(colorizer, editor, tab.Mode)` once; the switch handles CSharp/Xml/Json/None. Keeps both `TextChanged` handler and initial invocation on the same code path — no duplication.
- **`RoslynColorizer` stays attached always**: line transformer added once per editor in `_bindings` init. For non-CSharp modes, `colorizer.Clear()` empties `_spans`; `ColorizeLine`'s foreach exits immediately. No add/remove churn per file switch.
- **Both color sources cleared on every mode switch**: any transition explicitly clears the *other* source (`editor.SyntaxHighlighting = null` on CSharp, `colorizer.Clear()` on Xml/Json/None). Prevents residual color bleeding when the same TextEditor is rebound to a different tab.
- **AvaloniaEdit definition names** (verified via scratch enumerator on `Avalonia.AvaloniaEdit` 12.0.0): `"XML"` (all caps) and `"Json"` (title case). Case-exact when passed to `HighlightingManager.Instance.GetDefinition`.
- **Xshd readability tuning applied inline** (Open Decision #4 → deviation from default): stock palettes shipped illegible on the dark theme — `Json.Punctuation` was literally `Black`, making braces/brackets/commas invisible on `#000000` bg. Verified via scratch enumerator dumping `NamedHighlightingColors.Foreground`. Added `Views/XshdDarkPalette.cs` with `Tune(name)` — mutates the shared `IHighlightingDefinition` once per name, reassigning foregrounds to a VS-Code-dark-aligned palette (comment green `#6A9955`, string orange `#CE9178`, keyword blue `#569CD6`, num green `#B5CEA8`, property light-blue `#9CDCFE`, punctuation `#DCDCDC` matching editor default). Structural chars that xshd doesn't name still inherit `editor.Foreground = #DCDCDC` and stay legible.
- **`HighlightingManager.Instance.GetDefinition` returns a shared instance**: mutating `NamedHighlightingColors[i].Foreground` persists globally. `XshdDarkPalette.Tune` guards with a `HashSet<string>` so overrides apply once per name.
- **Stock xshd named colors, verified via scratch enumerator on `Avalonia.AvaloniaEdit` 12.0.0**:
  - XML: `Comment` (Green), `CData` (Blue), `DocType` (Blue), `XmlDeclaration` (Blue), `XmlTag` (DarkMagenta), `AttributeName` (Red), `AttributeValue` (Blue), `Entity` (Teal), `BrokenEntity` (Olive).
  - Json: `Bool` (Blue), `Number` (Red), `String` (Green), `Null` (Olive), `FieldName` (DarkMagenta), `Punctuation` (Black).
- **Pre-existing unused `using System.IO;`** in `MainWindow.axaml.cs` was surfaced as a new LSP diagnostic during this ticket; left alone to keep scope tight. Candidate for a general cleanup pass.
- **Related area affected**: `.axaml` files (`App.axaml`, `Views/MainWindow.axaml`) now colorize as XML. XAML-specific bits (namespaces, `{Binding …}` markup extensions) render as plain XML attribute values — expected, worth noting for the MSBuild-aware / XAML-aware follow-up.
- **Rejected alternative — extension-based mapping inline in `EditorTabViewModel` ctor**: rejected per Open Decision #2. Static helper next to the enum keeps mapping reusable and self-documenting.
- **Rejected alternative — auto-detection by content**: explicitly out of scope. Extension is source of truth. Files with no/unknown extension render as plain text (`HighlightMode.None`), which is the desired fallback.
