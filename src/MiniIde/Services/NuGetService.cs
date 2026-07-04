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
