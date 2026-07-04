# NuGet approach

## Do

- **Version queries / feed browse** → `NuGet.Protocol` + `NuGet.Versioning`. Use `SourceRepository` + `PackageMetadataResource`.
- **Install / upgrade** → edit `.csproj` `<PackageReference>` XML directly, then shell `dotnet restore`.

## Don't

- `NuGet.PackageManagement` — .NET Framework only, not netstandard. Painful, mostly exists for legacy `packages.config`. SDK-style projects don't need it.

## Rationale

VS uses full `PackageManagement` because of legacy `packages.config` support. Modern SDK-style projects: XML edit + `dotnet restore` covers install/upgrade cleanly.

Refs: [NuGet Client SDK](https://learn.microsoft.com/en-us/nuget/reference/nuget-client-sdk), [Björkström — NuGet v3 libraries](https://martinbjorkstrom.com/posts/2018-09-19-revisiting-nuget-client-libraries).
