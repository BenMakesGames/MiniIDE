using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniIde.ViewModels;

/// <summary>One selectable row in a project's Run flyout — a build configuration or a launch profile.
/// <see cref="IsSelected"/> is the only mutable member on purpose: picking a row updates the existing rows in
/// place rather than rebuilding the collection, so the click that caused it is never standing on a row that
/// disappears underneath it.</summary>
public partial class RunChoice : ObservableObject
{
    public RunChoice(string label, string? value, string group, ICommand select, bool isSelected)
    {
        Label = label;
        Value = value;
        Group = group;
        Select = select;
        _isSelected = isSelected;
    }

    public string Label { get; }

    /// <summary>What picking this row selects: a configuration name, a profile name, or null for the
    /// "let <c>dotnet run</c> choose" profile row.</summary>
    public string? Value { get; }

    /// <summary>The radio group this row belongs to. Unique per project and axis: every card's flyout lives in
    /// the same window, and a group name shared across cards would let one card's pick uncheck another's.</summary>
    public string Group { get; }

    public ICommand Select { get; }

    [ObservableProperty] private bool _isSelected;
}
