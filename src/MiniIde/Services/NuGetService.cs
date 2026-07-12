using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using MiniIde.Models;

namespace MiniIde.Services;

public class NuGetService
{
    private readonly SourceRepository _repo = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
    private readonly SourceCacheContext _cache = new();
    private readonly ILogger _log = NullLogger.Instance;

    public static IReadOnlyList<PackageEntry> ReadReferences(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        var result = new List<PackageEntry>();
        foreach (var el in doc.Descendants("PackageReference"))
        {
            var id = (string?)el.Attribute("Include");
            var ver = (string?)el.Attribute("Version") ?? (string?)el.Element("Version");
            if (id is null) continue;
            result.Add(new PackageEntry(csprojPath, id, ver ?? ""));
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, bool includePrerelease, CancellationToken ct = default)
    {
        var res = await _repo.GetResourceAsync<MetadataResource>(ct);
        var versions = await res.GetVersions(packageId, includePrerelease, includeUnlisted: false, _cache, _log, ct);
        return versions.OrderByDescending(v => v).Select(v => v.ToNormalizedString()).ToList();
    }

    /// <summary>Fetches feed metadata for the exact <c>(packageId, version)</c> pair. Returns <c>null</c> when
    /// the feed has no matching entry (private-feed package, pulled from the feed later, or a typo). The caller
    /// surfaces that via its status path — <see cref="NuGetViewModel"/> refuses to open an empty tab on null.
    /// Throws on network / parse failure; the caller catches. <c>includePrerelease</c>/<c>includeUnlisted</c>
    /// are both true so a version pinned in the .csproj is still resolvable even if it was later unlisted.</summary>
    public async Task<IPackageSearchMetadata?> GetMetadataAsync(string packageId, string version, CancellationToken ct = default)
    {
        var res = await _repo.GetResourceAsync<PackageMetadataResource>(ct);
        var identity = new PackageIdentity(packageId, NuGetVersion.Parse(version));
        return await res.GetMetadataAsync(identity, _cache, _log, ct);
    }

    /// <summary>Plain-text formatter for the metadata tab. No truncation — 30-TFM dependency blobs are fine
    /// (the tab is read-only text and line-caps at 5000 lines, far beyond any real package). Prefers rich
    /// fields (<c>Description</c>, <c>LicenseMetadata.License</c>) but falls back to their older cousins
    /// (<c>Summary</c>, <c>LicenseUrl</c>) when empty.</summary>
    public static string FormatMetadata(IPackageSearchMetadata md)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Id: {md.Identity.Id}");
        sb.AppendLine($"Version: {md.Identity.Version.ToNormalizedString()}");
        if (!string.IsNullOrWhiteSpace(md.Authors)) sb.AppendLine($"Authors: {md.Authors}");

        var description = !string.IsNullOrWhiteSpace(md.Description) ? md.Description : md.Summary;
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.AppendLine();
            sb.AppendLine("Description");
            foreach (var line in description!.Replace("\r\n", "\n").Split('\n'))
                sb.AppendLine($"  {line}");
        }

        if (md.ProjectUrl is not null) sb.AppendLine($"Project URL: {md.ProjectUrl}");

        var license = md.LicenseMetadata?.License;
        if (string.IsNullOrWhiteSpace(license) && md.LicenseUrl is not null) license = md.LicenseUrl.ToString();
        if (!string.IsNullOrWhiteSpace(license)) sb.AppendLine($"License: {license}");

        if (!string.IsNullOrWhiteSpace(md.Tags)) sb.AppendLine($"Tags: {md.Tags}");
        if (md.Published is { } published) sb.AppendLine($"Published: {published:yyyy-MM-dd}");
        if (md.ReadmeUrl is not null) sb.AppendLine($"Readme: {md.ReadmeUrl}");

        var deps = md.DependencySets?.ToList();
        if (deps is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Dependencies");
            foreach (var set in deps)
            {
                sb.AppendLine($"  {set.TargetFramework.GetShortFolderName()}");
                if (set.Packages is null || !set.Packages.Any())
                {
                    sb.AppendLine("    (none)");
                    continue;
                }
                foreach (var p in set.Packages)
                    sb.AppendLine($"    {p.Id} ({p.VersionRange.PrettyPrint()})");
            }
        }

        return sb.ToString();
    }

    public static void SetVersion(string csprojPath, string packageId, string newVersion)
    {
        var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        foreach (var el in doc.Descendants("PackageReference"))
        {
            if ((string?)el.Attribute("Include") != packageId) continue;
            if (el.Attribute("Version") is { } a) a.Value = newVersion;
            else if (el.Element("Version") is { } e) e.Value = newVersion;
            else el.SetAttributeValue("Version", newVersion);
        }
        doc.Save(csprojPath, SaveOptions.DisableFormatting);
    }

    public static async Task<int> RestoreAsync(string csprojPath, Action<string> log, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(csprojPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("restore");
        psi.ArgumentList.Add(csprojPath);
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
