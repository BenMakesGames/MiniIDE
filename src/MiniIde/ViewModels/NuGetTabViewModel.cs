using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

/// <summary>One project's NuGet packages, in a tab of their own: what it references, the versions available for
/// the selected package, and Apply + Restore.</summary>
public partial class NuGetTabViewModel : TabViewModelBase
{
    private readonly NuGetService _svc;
    private readonly Func<OutputTabViewModel> _resolveOutput;
    private readonly Func<string, string, OutputTabViewModel> _resolveMetadataTab;
    private readonly string _projectPath;
    private readonly string _projectName;

    public ObservableCollection<PackageEntry> Packages { get; } = new();
    public ObservableCollection<string> Versions { get; } = new();

    [ObservableProperty] private PackageEntry? _selectedPackage;
    [ObservableProperty] private string? _selectedVersion;
    [ObservableProperty] private bool _includePrerelease;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    public override string Header => $"{_projectName} - NuGet";

    // The delegates get-or-create + activate a tab, keeping this VM ignorant of tab mechanics. _resolveOutput
    // owns the shared "NuGet - Output" restore tab (resolved lazily per Apply). _resolveMetadataTab takes an
    // arbitrary (tabId, header) so each package gets its own tab.
    public NuGetTabViewModel(
        ProjectEntry project,
        NuGetService svc,
        Func<OutputTabViewModel> resolveOutput,
        Func<string, string, OutputTabViewModel> resolveMetadataTab)
        : base(TabIdFor(project.Path), filePath: null)
    {
        _svc = svc;
        _resolveOutput = resolveOutput;
        _resolveMetadataTab = resolveMetadataTab;
        _projectPath = project.Path;
        _projectName = project.Name;
        ReloadPackages();
    }

    /// <summary>One NuGet tab per project: re-asking for a project's packages re-activates its existing tab.</summary>
    public static string TabIdFor(string projectPath) => "nuget-packages:" + Path.GetFullPath(projectPath).ToLowerInvariant();

    /// <summary>Re-reads the project file's package references. Called after our own Apply, and by the owner when
    /// the project file changes on disk — the list is only ever what the file says right now. The selected
    /// package is re-selected by id, so an external edit doesn't throw away the versions being looked at.</summary>
    public void ReloadPackages()
    {
        var selectedId = SelectedPackage?.Id;
        Packages.Clear();
        try { foreach (var p in NuGetService.ReadReferences(_projectPath)) Packages.Add(p); }
        catch (Exception ex) { Status = ex.Message; return; }

        PackageEntry? reselect = null;
        foreach (var p in Packages)
            if (string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase)) reselect = p;
        SelectedPackage = reselect;
    }

    partial void OnSelectedPackageChanged(PackageEntry? value) => _ = LoadVersionsAsync();
    partial void OnIncludePrereleaseChanged(bool value) => _ = LoadVersionsAsync();

    private async Task LoadVersionsAsync()
    {
        Versions.Clear();
        if (SelectedPackage is null) return;
        IsBusy = true;
        Status = $"Loading {SelectedPackage.Id}...";
        try
        {
            var list = await _svc.GetVersionsAsync(SelectedPackage.Id, IncludePrerelease, CancellationToken.None);
            foreach (var v in list) Versions.Add(v);
            Status = $"{list.Count} version(s)";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Fetches feed metadata for a package's *installed* version, formats it, and drops the result into
    /// a package-specific read-only tab (id <c>nuget-meta:{id-lower}</c>). Always clears + re-appends on every
    /// call — the tab dedups, so a re-double-click after Apply refreshes stale content. Feed miss (null) reports
    /// via <see cref="Status"/> without opening an empty tab. Not cached: on-demand fetches run in the background
    /// and the user's next double-click is the natural refresh trigger.</summary>
    public async Task OpenMetadataAsync(PackageEntry package)
    {
        IsBusy = true;
        Status = $"Loading {package.Id}...";
        try
        {
            var md = await _svc.GetMetadataAsync(package.Id, package.CurrentVersion, CancellationToken.None);
            if (md is null) { Status = $"No metadata found for {package.Id} {package.CurrentVersion}"; return; }
            var formatted = NuGetService.FormatMetadata(md);
            var tab = _resolveMetadataTab($"nuget-meta:{package.Id.ToLowerInvariant()}", $"{package.Id} — NuGet");
            tab.Clear();
            tab.Append(formatted);
            Status = $"{package.Id} metadata loaded";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (SelectedPackage is null || SelectedVersion is null) return;
        IsBusy = true;
        Status = "Applying...";
        try
        {
            NuGetService.SetVersion(SelectedPackage.ProjectPath, SelectedPackage.Id, SelectedVersion);
            var output = _resolveOutput();
            output.Clear();
            output.Append($"[nuget] {SelectedPackage.Id} -> {SelectedVersion}");
            var code = await NuGetService.RestoreAsync(SelectedPackage.ProjectPath, output.Append, CancellationToken.None);
            Status = code == 0 ? "Restored" : $"Restore failed ({code})";
            ReloadPackages();
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
}
