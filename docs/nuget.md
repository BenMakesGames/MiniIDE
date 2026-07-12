# NuGet approach

## Do

- **Version queries / feed browse** → `NuGet.Protocol` + `NuGet.Versioning`. Use `SourceRepository` + `PackageMetadataResource`.
- **Install / upgrade** → edit `.csproj` `<PackageReference>` XML directly, then shell `dotnet restore`.

## Don't

- `NuGet.PackageManagement` — .NET Framework only, not netstandard. Painful, mostly exists for legacy `packages.config`. SDK-style projects don't need it.

## Rationale

VS uses full `PackageManagement` because of legacy `packages.config` support. Modern SDK-style projects: XML edit + `dotnet restore` covers install/upgrade cleanly.

Refs: [NuGet Client SDK](https://learn.microsoft.com/en-us/nuget/reference/nuget-client-sdk), [Björkström — NuGet v3 libraries](https://martinbjorkstrom.com/posts/2018-09-19-revisiting-nuget-client-libraries).

## API gotchas

- `PackageMetadataResource.GetMetadataAsync` has two overloads with divergent signatures. `(string id, bool includePrerelease, bool includeUnlisted, SourceCacheContext, ILogger, CancellationToken)` returns *all* versions; `(PackageIdentity identity, SourceCacheContext, ILogger, CancellationToken)` returns a single pinned version and has **no** prerelease/unlisted bools — the identity's version already resolves listed and unlisted alike. Reaching for `.GetMetadataAsync(identity, includePrerelease: ..., includeUnlisted: ..., ...)` is a `CS1503` (`PackageIdentity → string`).
