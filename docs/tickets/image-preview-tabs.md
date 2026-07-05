# Image Preview Tabs

## Context
**Current behavior**: `EditorTabViewModel` unconditionally `File.ReadAllText` for any opened file. Binary images (`.png`, `.jpg`, `.jpeg`, `.bmp`, `.webp`, `.gif`) render as UTF-8 gibberish in `TextEditor`.

**New behavior**: Opening a raster image file opens a dedicated image-preview tab that renders the decoded bitmap fit-to-view. Other file types unchanged.

## Prerequisites
None.

## Scope
### In scope
- New tab-VM base type; extract from `EditorTabViewModel`.
- New `ImageTabViewModel` — loads `Avalonia.Media.Imaging.Bitmap` in ctor.
- `MainWindowViewModel.Tabs` / `ActiveTab` widened to base type.
- `MainWindowViewModel.OpenFileAsync` branches on extension.
- `MainWindow.axaml` — replace single `TabControl.ContentTemplate` with typed `TabControl.DataTemplates` (Editor + Image).
- Fix cast sites downstream: `MainWindow.axaml.cs` `OnCloseTabClick`, `GetTargetPath`, `SaveActiveAsync`.

### Out of scope
- `.ico` — SkiaSharp (Avalonia's decoder) doesn't support ICO. Skip; keep binary-as-text for now.
- `.svg` — needs `Avalonia.Svg.Skia`. Skip.
- Pan / zoom / dimensions overlay / file-size label.
- Image size cap. Trust decoder.
- Editing / re-encoding image content.

## Relevant Docs & Anchors
- **Related tickets**: `docs/tickets/complete/2026-07-05 xml-json-syntax-highlighting.md` — precedent for enum + extension dispatch.
- **Code anchors**:
  - `ViewModels/EditorTabViewModel.cs` — current tab VM; split base out of this.
  - `ViewModels/MainWindowViewModel.cs` — `Tabs`, `ActiveTab`, `OpenFileAsync`, `CloseTabAsync`.
  - `Views/MainWindow.axaml` — `TabControl` at `Grid.Column="2"`, its `ItemTemplate` + `ContentTemplate`.
  - `Views/MainWindow.axaml.cs` — `OnCloseTabClick`, `GetTargetPath`, `SaveActiveAsync`, `FindActiveEditor`, `OpenHit`.
  - `Models/FileIconMap.FromExtension` — existing image-extension list (mirror scope from there minus `.ico` + `.svg`).

## Constraints & Gotchas
- `FindActiveEditor` finds a `TextEditor` bound to `Vm.ActiveTab`. Returns null when active tab is `ImageTabViewModel` — callers (`OpenHit`, `GoToDefinitionAsync`, `FindRefsAsync`) already early-return on null. No new guards needed.
- `CloseTabAsync` auto-saves dirty tabs. `ImageTabViewModel.IsDirty` stays false → `SaveAsync` never invoked, but base must still expose `SaveAsync` for polymorphic call from Ctrl+S handler.
- `Avalonia.Media.Imaging.Bitmap` ctor throws on decode failure. Wrap; store `Error` string for display panel.
- `TabControl.DataTemplates` picks by runtime type. Both templates must be typed `DataType="vm:EditorTabViewModel"` / `DataType="vm:ImageTabViewModel"` — not the base. Item template (tab header) can target base since `Header` lives on base.
- `rg`-driven Find results skip binary by default — image files won't appear in search hits, so no `OpenHit` regression on image paths.

## Open Decisions
1. **Base type — abstract class vs. interface** — abstract class simpler (fields + `partial` observable props). Default: abstract class `TabViewModelBase : ViewModelBase`.
2. **Image content template — Viewbox vs. Image with `Stretch="Uniform"`** — either fits. Default: Viewbox around `Image` — trivial fit-to-view, no manual sizing.
3. **Error display when decode fails** — swap image for `TextBlock` inside same template (via `IsVisible` bindings) vs. separate template. Default: single template, dual-child Grid with `IsVisible` toggling on null/non-null of `Image`/`Error`.
4. **Extension → tab-kind dispatch location** — new helper on `TabViewModelBase` static side, vs. inline `switch` in `OpenFileAsync`, vs. new `TabKindExtensions.FromExtension`. Default: static helper `TabViewModelBase.CreateForFile(string path)` factory — one obvious home, mirrors `HighlightModeExtensions.FromExtension` precedent.

## Acceptance Criteria
- [ ] `TabViewModelBase` abstract type exists with `FilePath`, `Header`, `IsDirty`, `SaveAsync`, `CloseCommand`, `RequestClose`.
- [ ] `EditorTabViewModel` inherits `TabViewModelBase`; existing text-editor behavior unchanged.
- [ ] `ImageTabViewModel` inherits `TabViewModelBase`; exposes `Bitmap? Image` and `string? Error`; `SaveAsync` is a no-op returning completed task; `IsDirty` never set.
- [ ] `MainWindowViewModel.Tabs` typed `ObservableCollection<TabViewModelBase>`; `ActiveTab` typed `TabViewModelBase?`.
- [ ] Opening `.png`, `.jpg`, `.jpeg`, `.bmp`, `.webp`, `.gif` creates an `ImageTabViewModel`; other extensions create `EditorTabViewModel`. Case-insensitive.
- [ ] Image tab renders decoded image fit-to-view, centered, on the tab's dark background.
- [ ] Corrupt / unreadable image file → tab opens showing error text (e.g. `"Failed to decode: <message>"`) instead of crashing.
- [ ] Ctrl+S while an image tab is active: no exception, no file write, no error dialog.
- [ ] Close button ✕ on an image tab removes the tab; active-tab fallback behavior identical to editor tabs.
- [ ] Tab context menu (Open in Explorer, Copy absolute path, Copy relative path) works on image tabs.
- [ ] Opening the same image path twice reuses the existing image tab (mirror existing `OpenFileAsync` dedup).

## Implementation

### 1. Extract `TabViewModelBase`
New file `ViewModels/TabViewModelBase.cs`. Abstract class deriving `ViewModelBase`. Members:
- `string FilePath { get; }` — set from ctor arg.
- `virtual string Header` — default `Path.GetFileName(FilePath) + (IsDirty ? " *" : "")`. Override point for future variants.
- `[ObservableProperty] bool _isDirty`.
- `abstract Task SaveAsync()`.
- `event Func<TabViewModelBase, Task>? RequestClose`.
- `[RelayCommand] Task CloseAsync() => RequestClose?.Invoke(this) ?? Task.CompletedTask`.
- `OnIsDirtyChanged` → `OnPropertyChanged(nameof(Header))`.
- Static factory `TabViewModelBase CreateForFile(string path)` — switches on lowercased extension; image extensions → `new ImageTabViewModel(path)`; else → `new EditorTabViewModel(path)`. Image extension list: `.png .jpg .jpeg .bmp .webp .gif`.

### 2. Rework `EditorTabViewModel`
Inherit `TabViewModelBase`. Move `FilePath`, `IsDirty`, `Header`, `CloseCommand`, `RequestClose`, `OnIsDirtyChanged` to base — delete from here. Keep `Document`, `Mode`, `CaretOffset`, `SaveAsync` (override — writes `Document.Text`). Ctor still calls `File.ReadAllText`. Adapt `RequestClose` invocation site: base's event fires; nothing to change here beyond signature widening on the delegate (now `Func<TabViewModelBase, Task>`).

### 3. New `ImageTabViewModel`
New file `ViewModels/ImageTabViewModel.cs`. Inherits `TabViewModelBase`.
- `Bitmap? Image { get; }` — try/catch `new Bitmap(FilePath)` in ctor; on exception store `Error`.
- `string? Error { get; }`.
- `override Task SaveAsync() => Task.CompletedTask`.

### 4. Widen `MainWindowViewModel` tab plumbing
`Tabs` → `ObservableCollection<TabViewModelBase>`. `ActiveTab` → `TabViewModelBase?`. `OpenFileAsync`: replace `new EditorTabViewModel(path)` with `TabViewModelBase.CreateForFile(path)`. `CloseTabAsync` signature accepts `TabViewModelBase`. Match dedup loop's cast type accordingly.

### 5. Split `TabControl` templates in `MainWindow.axaml`
Keep `ItemTemplate` targeting a common tab-header shape; retype its `DataType` to `vm:TabViewModelBase` (or omit and rely on base binding — verify Avalonia handles). Replace `ContentTemplate` block with `TabControl.DataTemplates`:
- One typed `DataType="vm:EditorTabViewModel"` — existing `ae:TextEditor` block, unchanged.
- One typed `DataType="vm:ImageTabViewModel"` — root `Grid` with two children: `Viewbox` wrapping `Image Source="{Binding Image}"` (`IsVisible="{Binding Image, Converter=...NotNull}"` or negative bind on `Error`); `TextBlock Text="{Binding Error}"` centered, `IsVisible` gated on non-null `Error`. Background `#1E1E22` to match. Use built-in `x:Null` object comparison or an inline value converter — prefer bind-to-`Error`-null-visibility idiom used elsewhere in repo if one exists; else the smallest working converter.

### 6. Fix cast sites in `MainWindow.axaml.cs`
- `OnCloseTabClick`: cast `Tag` to `TabViewModelBase` (was `EditorTabViewModel`).
- `GetTargetPath`: add arm `TabViewModelBase tab => tab.FilePath` covering both editor + image; drop the `EditorTabViewModel`-specific arm.
- `SaveActiveAsync`: unchanged behavior — `Vm.ActiveTab?.SaveCommand.ExecuteAsync(null)` works via base `SaveAsync` override; image tab's is no-op.
- `FindActiveEditor`: no change; still queries for `TextEditor` bound to `Vm.ActiveTab`. Returns null for image tabs. Callers already handle null.

### 7. Verify no orphan `EditorTabViewModel` refs
Grep `EditorTabViewModel` after refactor. Remaining refs should be: `EditorTabViewModel.cs`, `TabViewModelBase.CreateForFile` factory, `MainWindow.axaml` `EditorTabViewModel`-typed DataTemplate + editor binding code in `.axaml.cs` (`OnEditorAttached`, `OnEditorDataContextChanged`, `BindEditor` — these are TextEditor-specific, correct). No refs in `MainWindowViewModel` outside factory.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds; no new warnings.
- [ ] Launch via `scripts/run.ps1`.
- [ ] Open a `.cs` file — colorization + edit + save behavior identical to before (regression).
- [ ] Open a `.png` from the tree — new tab shows rendered image, fit-to-view, centered.
- [ ] Open a `.jpg` — renders.
- [ ] Open a `.bmp` — renders.
- [ ] Open a `.webp` — renders (Skia supports; confirm on real file).
- [ ] Open a `.gif` — renders first frame (Avalonia Bitmap is single-frame; acceptable).
- [ ] Open a `.svg` — still opens as text (out of scope; regression check that dispatch didn't sweep it in).
- [ ] Open a `.ico` — still opens as text (out of scope).
- [ ] Rename a `.txt` file to `.png`, open — error panel shows `"Failed to decode: ..."`; app does not crash.
- [ ] Ctrl+S while image tab active — no exception; status bar unchanged.
- [ ] Close ✕ on image tab — tab removed; active tab falls back to first remaining tab (or null).
- [ ] Right-click image tab header → Open in Explorer / Copy absolute path / Copy relative path — all three work.
- [ ] Open same image path twice (double-click, then double-click again) — no duplicate tab; existing tab activates.
- [ ] With both an editor tab and an image tab open, switch between them repeatedly — image renders each activation; editor content stays intact.
