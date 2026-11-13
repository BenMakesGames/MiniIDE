# Stack

- **Runtime**: .NET 10
- **GUI**: Avalonia + AvaloniaEdit (output tabs only)
- **Solution parse**: `Microsoft.VisualStudio.SolutionPersistence` (metadata only; no MSBuild — see `msbuild.md`)
- **Run / test**: shell `dotnet run` / `dotnet test`
- **NuGet**: `NuGet.Protocol` + XML edit + shell `dotnet restore`

## GUI framework decision

Avalonia over WPF: cross-plat free bonus, AvaloniaEdit handles large streamed logs well.

Skipped: WinUI 3 (weak text editing), Electron/Tauri (rebuilds IPC + editor for no reason).
