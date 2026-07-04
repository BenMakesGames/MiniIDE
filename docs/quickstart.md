# Quickstart

## Run
```
scripts/run.ps1    # build + launch, detaches
scripts/stop.ps1   # kill running MiniIde
```

Or direct: `dotnet run --project src/MiniIde`.

## Shortcuts
- Ctrl+O — Open solution (.slnx or .sln)
- Ctrl+S — Save active tab
- Ctrl+Shift+F — Focus Find box
- F5 — Play (dotnet run startup project)
- F12 — Go to Definition (triggers workspace load on first use)
- Shift+F12 — Find References (same)

## First-run flow
1. Ctrl+O → pick .slnx.
2. Solution tree left. Double-click project → files load. Double-click .cs → editor tab.
3. Ctrl+Shift+F → type, Enter. Regex checkbox optional.
4. NuGet tab: pick project → package → version → Apply + Restore.
5. Startup dropdown → Play.

## Design gotchas
- MSBuildWorkspace loads lazily — first F12 / Shift+F12 will be slow (seconds to minutes).
- Global find shells `rg` (must be on PATH).
- `.slnx` requires Roslyn 5.6.0+ (pinned).
