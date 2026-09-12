# "Open in External App" Dead-Ends on a Broken File Association

## Context
**Current behavior**: *Open in external app* silently does nothing for a file whose extension has a **broken** association — one registered to an application that is no longer installed. `ShellService.OpenWithRegisteredApp` launches the path with `UseShellExecute = true`, and its only fallback is `catch (Win32Exception ex) when (ex.NativeErrorCode == 1155)` → retry with `Verb = "openas"`. A broken association does not raise `1155`; it raises `CO_E_APPNOTFOUND` (`0x800401F5`, surfaced as `NativeErrorCode == -2147221003`). The filter misses it, the exception propagates to `OnCtxOpenExternallyClick`, and the only feedback is a line on `Vm.Status` that reads as nothing having happened.

**New behavior**: the action asks Windows to open the file and lets Windows own the outcome — including raising its own *"How do you want to open this file?"* picker — instead of MiniIDE classifying shell error codes and reproducing the shell's decision tree. This is one property (`ErrorDialog = true`), and it **removes** the error-code filter and the `openas` fallback rather than extending them.

The current design was reasoned from two premises recorded in `docs/tickets/complete/2026-08-20 open-file-in-external-app.md` that have since been disproved by direct measurement on the dev machine — see Constraints & Gotchas.

## Prerequisites
None.

## Scope
### In scope
- `ShellService.OpenWithRegisteredApp` — set `ErrorDialog = true`, accept an owner window handle, delete the `openas` fallback and the `ErrorNoAssociation` constant.
- `Views/MainWindow.axaml.cs` — `OnCtxOpenExternallyClick` moves the launch off the UI thread and supplies the owner `HWND`.
- `docs/avalonia.md` — rewrite the shell-execute-with-registered-app bullet, whose current guidance is the bug.

### Out of scope
- **`ShellService.RevealInExplorer` and `ShellService.OpenTerminalWithClaude`.** Confirmed with the requester. Both launch a *named* executable rather than resolving an association, so neither can reach the broken-association class; `OpenTerminalWithClaude` already handles its own "not installed" case with the `wt`→PowerShell fallback.
- **Making launch failures more visible generally.** All three `ShellService` launches report failure only via `Vm.Status`, which is why this bug read as "nothing happens". Worth a follow-up ticket; not this one.
- **Repairing the dev machine's `.cs` association.** An environment problem, not a code one (see Test Plan).
- **Any menu, gating, or icon change.** The item, its placement, and its enablement rules are unchanged.
- **Non-Windows behavior.** `ShellService` is Windows-only by design.

## Relevant Docs & Anchors
- **Design docs**: `docs/avalonia.md` — the shell-execute bullets (the registered-app one is the bullet this ticket rewrites; the visible-terminal one directly above it stays).
- **Related tickets**:
  - `docs/tickets/complete/2026-08-20 open-file-in-external-app.md` — the feature this fixes. Its *Rejected alternatives* explicitly parked `rundll32 shell32.dll,OpenAs_RunDLL` as "the escape hatch if `openas` ever proves unreliable"; that escape hatch is **not** needed, because `ErrorDialog = true` gets the same picker with no extra process.
  - `docs/tickets/complete/2026-07-10 extract-shell-service.md` — `ShellService`'s contract: it throws, and the caller surfaces the failure on `Vm.Status`.
- **Code anchors**: `ShellService.OpenWithRegisteredApp` and its `ErrorNoAssociation` constant; `OnCtxOpenExternallyClick` and `GetTargetPath` in `Views/MainWindow.axaml.cs`; the `async void` context-menu handlers alongside it (`OnCtxCopyAbsolutePathClick` is the closest shape) for the await pattern to mirror.

## Constraints & Gotchas
- **`ProcessStartInfo.ErrorDialog` defaults to `false`, and that is what suppresses the picker.** .NET builds the `ShellExecuteEx` mask as `if (startInfo.ErrorDialog) shellExecuteInfo.hwnd = startInfo.ErrorDialogParentHandle; else shellExecuteInfo.fMask |= SEE_MASK_FLAG_NO_UI;` (dotnet/runtime, `Process.Win32.cs`). Every launch MiniIDE makes has been telling the shell not to show UI.
- **The launch blocks the calling thread for the entire life of the picker.** Measured on the dev machine: `Process.Start` returned only when the dialog closed — 8683 ms unparented, 10772 ms parented. `OnCtxOpenExternallyClick` runs on the Avalonia UI thread, so the call **must** move off it or MiniIDE freezes while the picker is open. This is the single most important part of the change; getting `ErrorDialog = true` right and leaving the call on the UI thread trades a silent no-op for a visible hang.
- **Dismissing the picker throws `Win32Exception` with `NativeErrorCode == 1223` (`ERROR_CANCELLED`).** That is a deliberate user choice, not a failure, and must not reach `Vm.Status` as *"Open in external app failed…"*.
- **`Verb = "openas"` is not a general fallback and its removal is not a regression.** `openas` is registered only on the `Unknown` ProgID (`HKCR\Unknown\shell\openas` → `OpenWith.exe "%1"`); there is no `HKCR\*\shell\openas`. Measured: `openas` on a `.cs` file throws `1155` itself. It could only ever work for extensions with no ProgID at all — exactly the ones the shell already handles unaided.
- **Two Learnings in `2026-08-20 open-file-in-external-app.md` are now disproved** and should not be trusted when reading it for background: "the `openas` fallback almost certainly never fires on this machine" (it is needed, and it fails) and "the fallback stays as a guarantee the action never dead-ends" (it dead-ends). The observation *around* them still holds: a truly **unregistered** extension does get Windows' own prompt with `Process.Start` succeeding.
- **`ShellService` must stay Avalonia-free** — its header states this. Take the owner window as an `IntPtr`; the view resolves the handle and passes it in.

## Open Decisions
1. **Where the off-UI-thread hop lives** — `await Task.Run(...)` in the handler, vs. an `async` method on `ShellService` that wraps its own body. Default: in the handler, keeping `ShellService` synchronous and matching how the other context-menu handlers already `await` their work.
2. **Whether `ERROR_CANCELLED` is swallowed in `ShellService` or in the handler** — service-side keeps every caller safe from it; handler-side keeps `ShellService` a pure pass-through of what the shell reported. Default: service-side, since "the user closed the picker" is a shell concept the service is already the one to know about.
3. **Whether to annotate the completed ticket** with a pointer to this one, given two of its Learnings are disproved. Default: leave completed tickets immutable and let `docs/avalonia.md` carry the corrected guidance, since that is what gets read during implementation.
4. **Parameter name and ordering** for the owner handle on `OpenWithRegisteredApp`. Default: implementer's call.

## Acceptance Criteria
- [ ] `ShellService.OpenWithRegisteredApp` launches the path with `UseShellExecute = true` **and** `ErrorDialog = true`, and sets `ErrorDialogParentHandle` from an owner handle supplied by the caller.
- [ ] The `openas` retry and the `ErrorNoAssociation` constant no longer exist anywhere in the codebase.
- [ ] `ShellService` contains no Avalonia or view-model reference; the owner window is accepted as an `IntPtr`.
- [ ] Invoking the action on a file whose extension resolves to an uninstalled application raises Windows' *"How do you want to open this file?"* picker instead of failing silently.
- [ ] Invoking the action on a file whose extension has a working association still launches that application directly, with no picker.
- [ ] Dismissing the picker without choosing an application leaves no *"failed"* text on `Vm.Status`.
- [ ] A launch failure that is neither a cancellation nor answerable by the picker still surfaces on `Vm.Status` naming the action.
- [ ] The MiniIDE window continues to repaint and respond while the picker is open — the launch does not occupy the UI thread.
- [ ] The picker is owned by the MiniIDE window and cannot be sent behind it.
- [ ] The `docs/avalonia.md` registered-app bullet describes the `ErrorDialog` mechanism and no longer prescribes catching `1155` and retrying with `openas`.

## Implementation

### 1. Hand the launch decision to Windows in `ShellService`
So MiniIDE stops reproducing the shell's decision tree, rewrite `OpenWithRegisteredApp` to set `ErrorDialog = true` alongside the existing `UseShellExecute = true`, and to take an owner window handle it assigns to `ErrorDialogParentHandle`. Delete the `try`/`catch (Win32Exception ex) when (...)` retry and the `ErrorNoAssociation` constant outright — with the shell's UI unsuppressed there is no error class left for MiniIDE to classify.

Catch the cancellation case only: a `Win32Exception` whose `NativeErrorCode` is `1223` means the user closed the picker, so return normally rather than throwing (Open Decision 2). Everything else still propagates, preserving the service's documented contract.

Rewrite the XML doc to match — it currently describes the `openas` fallback and the `ERROR_NO_ASSOCIATION` filter in detail, all of which is being removed. State instead that the shell owns the outcome, that the call blocks until any dialog it raises is dismissed (so callers must not invoke it on a UI thread), and that a user cancellation is not an error.

### 2. Call it off the UI thread, parented to the window
In `OnCtxOpenExternallyClick`, resolve the target with `GetTargetPath` as it does today, then resolve the owner handle from the window — `TopLevel.GetTopLevel(this)` exposes the platform handle, falling back to `IntPtr.Zero` if it is unavailable. Make the handler `async void` like its siblings and `await` the launch on a background thread (Open Decision 1) so the blocking shell call cannot freeze the UI while the picker is open.

Keep the existing `try`/`catch` reporting shape around it so a genuine failure still lands on `Vm.Status` naming this action.

### 3. Correct the reference doc
Rewrite the `docs/avalonia.md` bullet on handing a path to the registered program. Its current text prescribes exactly the broken design — catch `1155`, retry with `openas` — and would otherwise lead the next implementer straight back here. Replace it with the `ErrorDialog` mechanism, the reason the default suppresses the shell's UI, the fact that the call blocks until the dialog closes (hence: not on the UI thread), and `ERROR_CANCELLED` as the user-dismissed signal. Keep the existing closing note that shell-executing a **folder** just opens Explorer, so folders stay gated out.

## Test Plan
- [ ] `dotnet build MiniIde.slnx` succeeds with no new warnings, and `dotnet test MiniIde.slnx` passes.
- [ ] **Reproduce the bug first, before fixing.** On the dev machine `.cs` currently resolves to `VisualStudio.cs.14.0` (uninstalled) — confirm with `reg query "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.cs\UserChoice"`. If that association has since been repaired, the `.cs` cases below will not exercise the fix; find another broken extension, or verify via `AssocQueryString` that the extension returns `0x800401F5`.
- [ ] Launch via `scripts/run.ps1` with `MiniIde.slnx`. Right-click a `.cs` file in the tree → *Open in external app* → Windows' *"How do you want to open this .cs file?"* picker appears.
- [ ] While that picker is open, drag and resize the MiniIDE window → it repaints and responds; it is not frozen.
- [ ] Click MiniIDE while the picker is open → the picker stays in front of the window rather than disappearing behind it.
- [ ] Dismiss the picker with Escape, choosing nothing → the status bar shows no *"Open in external app failed"* text. **Do not click OK**, and if the picker offers an *"Always use this app…"* checkbox, leave it unchecked — confirming it permanently rewrites the association and destroys the test case.
- [ ] Regression: right-click a `.md` file (associated and working) → it opens directly in the registered editor with **no** picker.
- [ ] Regression: right-click a `.png` → opens directly in the registered image viewer.
- [ ] Regression: right-click a **folder** node → the item is still greyed and dimmed; *Open in Explorer* on that folder still works.
- [ ] Regression: run a project, right-click the output tab's header → the item is still greyed alongside the other path actions.
- [ ] Regression: the tab-header menu's *Open in external app* behaves identically to the tree's — both share the one handler.

## Learnings

### Architectural decisions
- **Open Decision 1 — where the thread hop lives**: default taken. `ShellService` stays synchronous and `OnCtxOpenExternallyClick` became `async void` with `await Task.Run(...)`, matching `OnCtxCopyAbsolutePathClick`. Keeping the service synchronous also keeps the "it blocks" fact explicit in its XML doc rather than hidden behind a `Task`.
- **Open Decision 2 — where `ERROR_CANCELLED` is swallowed**: default taken, service-side. Justified further by what measurement showed: cancellation is not one user gesture but several (see below), so the knowledge of "1223 means the user, not a failure" belongs in the one place that knows it is talking to the shell.
- **Open Decision 3 — annotating the old ticket**: default taken. `2026-08-20 open-file-in-external-app.md` is left untouched; `docs/avalonia.md` carries the corrected guidance.
- **Open Decision 4 — parameter shape**: `OpenWithRegisteredApp(string path, IntPtr ownerWindow)`. Owner second, since the path is the subject and the handle is context. `IntPtr` keeps `ShellService` Avalonia-free; the view resolves it via `TopLevel.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero`.

### Problems encountered
- **The first cut of the fix looked exactly like the bug it replaced** — click the item, nothing happens. The launch was correct; the *capture* was not. `await Task.Run(() => Vm.Shell.OpenWithRegisteredApp(target, owner))` evaluates `Vm` — i.e. `(MainWindowViewModel)DataContext!`, an `AvaloniaProperty` read — **on the threadpool thread**, which throws `InvalidOperationException: The calling thread cannot access this object because a different thread owns it`. The handler's own `catch` then routed it to `Vm.Status`, which is invisible (below), so the symptom was indistinguishable from the original silent dead-end. Fix: hoist `Vm.Shell` (and the handle) into locals on the UI thread and let the lambda capture those. Recorded as a new bullet in `docs/avalonia.md`.
- **Diagnosing it needed instrumentation, not inspection.** Every out-of-process harness (PowerShell calling `ShellExecuteEx` with the identical `ProcessStartInfo`, including from a threadpool thread with MiniIDE's own HWND as parent) raised the picker correctly, which ruled out the shell, the flags, the handle, and the thread hop — and left only something inside MiniIDE. A temporary `Console.Error.WriteLine` in the handler plus `Start-Process -RedirectStandardError` named the exception in one run.
- **`MainWindowViewModel.Status` is not rendered anywhere in the window.** All three `{Binding Status}` `TextBlock`s in `MainWindow.axaml` sit inside the Find / NuGet / Disk tab `DataContext`s and bind *those* view models' `Status`. So the ticket's premise that failures "read as nothing having happened" is stronger than stated: they are not merely easy to miss, they are not displayed at all. The acceptance criteria that mention `Vm.Status` are therefore verifiable by code inspection only. This is exactly the follow-up the ticket parked under *Making launch failures more visible generally* — it should now be written, and it is a bigger gap than "only via `Vm.Status`" suggests.

### Interesting tidbits
- **Measured, on this machine**: `.cs` → `VisualStudio.cs.14.0` (uninstalled) → `Win32Exception` with `NativeErrorCode == -2147221003` (`CO_E_APPNOTFOUND`), confirming the ticket's diagnosis and that the old `== 1155` filter could never match.
- **The blocking is total.** One deliberately-abandoned picker held `Process.Start` for **206 147 ms** before returning. The ticket's ~10 s figure was just how long that measurement's dialog sat open; there is no timeout.
- **Every dismissal path reports 1223, including clicking the owner window.** The picker (`OpenWith.exe`) is a light-dismiss window: it does not go behind the owner when the owner is clicked, it *closes*. So the acceptance criterion "cannot be sent behind it" holds, but by a mechanism the ticket did not anticipate — and it means the 1223 swallow is load-bearing for an ordinary stray click, not just for Escape.
- While a picker is open the owner window is disabled by the shell: clicks on the tree are ignored (though the window still repaints and can be moved/resized). Verified `Responding == True` and a `MoveWindow` completing in 2 ms with the picker up.

### Verification notes
- Automated: `dotnet build MiniIde.slnx` clean (0 warnings), `dotnet test MiniIde.slnx` 65/65.
- Manual, driven in the running app: broken `.cs` association → picker appears, centred on and in front of MiniIDE; window repaints and moves while it is open; dismissal is silent; `icon.ico` (working association) opens straight into IrfanView with no picker; a folder node still shows *Open in external app* greyed with *Open in Explorer* live. Both context menus share the one handler (`MainWindow.axaml:161` and `:215`).
- Not verified visually: the two `Vm.Status` criteria, because that property has no visible binding (above). Verified by code inspection instead.

### Rejected alternatives
- **Extending the error-code filter** to also catch `-2147221003` and retry: this is the design the ticket exists to delete — it keeps MiniIDE reimplementing the shell's decision tree and would fail again on the next unhandled code.
- **`rundll32 shell32.dll,OpenAs_RunDLL`** (parked as an escape hatch by the 2026-08-20 ticket): unnecessary. `ErrorDialog = true` produces the same picker with no extra process.
- **Making `ShellService.OpenWithRegisteredApp` async** to own its own thread hop: rejected per Open Decision 1, and it would have hidden the blocking behaviour that callers most need to know about.
