using Avalonia;
using System;
using Microsoft.Build.Locator;

namespace MiniIde;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!MSBuildLocator.IsRegistered)
        {
            try { MSBuildLocator.RegisterDefaults(); } catch { }
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
