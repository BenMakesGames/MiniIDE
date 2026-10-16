using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace MiniIde.ViewModels;

/// <summary>One clickable row in a project's Run flyout: a complete runnable selection (configuration plus
/// optionally a launch profile). Clicking it starts the run with that selection — no two-step pick-then-run,
/// no sticky state. The default selection carries a "(default)" suffix in its label so the plain Run button's
/// behavior is discoverable from the dropdown.</summary>
public sealed class RunChoice
{
    public RunChoice(string label, Action run)
    {
        Label = label;
        Run = new RelayCommand(run);
    }

    public string Label { get; }
    public ICommand Run { get; }
}
