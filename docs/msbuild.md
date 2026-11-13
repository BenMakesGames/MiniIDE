# MSBuild & solution parsing

No MSBuild (and no Roslyn) is loaded into the IDE. Everything that needs a build shells out to `dotnet`.

## Why no MSBuild in-process

- Loading MSBuild in-process (`MSBuildLocator` + `MSBuildWorkspace`) pins the SDK found at startup for the process's life: assemblies can't be unloaded.
- `MSBuildLocator` also sets `MSBUILD_EXE_PATH` / `MSBuildExtensionsPath` / `MSBuildSDKsPath` in the IDE's environment, which every child process inherits.
- An SDK update deletes the old SDK's folder → every `dotnet run` the IDE started failed with MSB4236 until restart.
- Before adding a feature that needs a project model (references, cross-file symbols), weigh that cost; prefer a `dotnet` shell call.

## .slnx

Metadata-only parse (no MSBuild eval) via `Microsoft.VisualStudio.SolutionPersistence`:

```csharp
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
var model = await SolutionSerializers.SlnXml.OpenAsync("My.slnx", ct);
foreach (var p in model.SolutionProjects) { /* p.FilePath */ }
```
