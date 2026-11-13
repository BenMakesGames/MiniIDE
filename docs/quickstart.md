# Quickstart

## Run
```
scripts/run.ps1    # build + launch, detaches
scripts/stop.ps1   # kill running MiniIde
```

Or direct: `dotnet run --project src/MiniIde`.

## Shortcuts
- Ctrl+O — Open solution (.slnx or .sln)

## First-run flow
1. Ctrl+O → pick .slnx.
2. Left panel: one card per project. ▶ Run runs it; ▿ picks the configuration (default Debug) and launch profile.
3. Each live run gets a button on its card → reopens its tab. Closing a run tab does **not** stop it; the tab's ■ Stop does.
4. Right-click a card → Manage NuGet packages, Open in Explorer / Windows Terminal, or copy its path.

## Design gotchas
- Closing the IDE kills every live run, including those whose tabs were closed.
