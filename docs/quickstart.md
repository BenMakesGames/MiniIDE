# Quickstart

## Run
```
scripts/run.ps1    # build + launch, detaches
scripts/stop.ps1   # kill running MiniIde
```

Or direct: `dotnet run --project src/MiniIde`.

## Shortcuts
- Ctrl+O — Open solution (.slnx or .sln)
- F12 — Go to Definition (triggers workspace load on first use)

## First-run flow
1. Ctrl+O → pick .slnx.
2. Left panel: one card per project. ▶ Run runs it; ▿ picks the configuration (default Debug) and launch profile.
3. Each live run gets a button on its card → reopens its tab. Closing a run tab does **not** stop it; the tab's ■ Stop does.
4. Right-click a card → Find problems (project-scoped errors/warnings tab) or Manage NuGet packages.
5. Problems tab: double-click a diagnostic → opens the file at that line.

## Design gotchas
- MSBuildWorkspace loads lazily — the first F12 / Find problems will be slow (seconds to minutes).
- `.slnx` requires Roslyn 5.6.0+ (pinned).
- Closing the IDE kills every live run, including those whose tabs were closed.
