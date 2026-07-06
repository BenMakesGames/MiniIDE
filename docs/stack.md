# Stack

- **Runtime**: .NET 9
- **GUI**: Avalonia + AvaloniaEdit
- **Language service**: Roslyn (embed direct)
- **Solution parse**: `Microsoft.VisualStudio.SolutionPersistence` (metadata) + `MSBuildWorkspace` (deferred)
- **NuGet**: `NuGet.Protocol` + XML edit + shell `dotnet restore`
- **Global find**: in-process `BenMakesGames.FileGrepper` (managed grep lib)

## GUI framework decision

Avalonia over WPF: cross-plat free bonus, AvaloniaEdit purpose-built for code editors, RoslynPad already validates stack.

Skipped: WinUI 3 (weak text editing), Electron/Tauri (rebuilds IPC + editor for no reason).
