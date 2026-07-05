using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.IO;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MiniIde.ViewModels;
using MiniIde.Views;

namespace MiniIde;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };

            var startupSolution = ResolveStartupSolution(desktop.Args);
            if (startupSolution is not null)
                Dispatcher.UIThread.Post(async () => await vm.OpenSolutionCommand.ExecuteAsync(startupSolution));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Picks the first launch argument that is an existing <c>.sln</c>/<c>.slnx</c> file
    /// and returns its full path, or null if none qualify. Backs the file association so
    /// double-clicking a solution in Explorer opens it here.
    /// </summary>
    private static string? ResolveStartupSolution(string[]? args)
    {
        if (args is null) return null;
        foreach (var arg in args)
        {
            var ext = Path.GetExtension(arg);
            var isSolution = ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
            if (isSolution && File.Exists(arg))
                return Path.GetFullPath(arg);
        }
        return null;
    }
}