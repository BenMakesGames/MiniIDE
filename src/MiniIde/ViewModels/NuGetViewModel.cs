using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

public partial class NuGetViewModel : ViewModelBase
{
    private readonly NuGetService _svc;
    private readonly Func<OutputViewModel> _resolveOutput;

    public ObservableCollection<ProjectEntry> Projects { get; } = new();
    public ObservableCollection<PackageEntry> Packages { get; } = new();
    public ObservableCollection<string> Versions { get; } = new();

    [ObservableProperty] private ProjectEntry? _selectedProject;
    [ObservableProperty] private PackageEntry? _selectedPackage;
    [ObservableProperty] private string? _selectedVersion;
    [ObservableProperty] private bool _includePrerelease;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    // The delegate gets-or-creates + activates the "NuGet - Output" tab and returns its buffer, keeping this
    // VM ignorant of tab mechanics. Resolved lazily per Apply so the tab appears only when a restore runs.
    public NuGetViewModel(NuGetService svc, Func<OutputViewModel> resolveOutput) { _svc = svc; _resolveOutput = resolveOutput; }

    public void SetProjects(System.Collections.Generic.IEnumerable<ProjectEntry> entries)
    {
        Projects.Clear();
        foreach (var e in entries) Projects.Add(e);
    }

    partial void OnSelectedProjectChanged(ProjectEntry? value)
    {
        Packages.Clear();
        Versions.Clear();
        if (value is null) return;
        try { foreach (var p in NuGetService.ReadReferences(value.Path)) Packages.Add(p); }
        catch (Exception ex) { Status = ex.Message; }
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
            var proj = SelectedProject;
            OnSelectedProjectChanged(proj);
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
}
