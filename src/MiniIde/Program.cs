using Avalonia;
using System;

namespace MiniIde;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Pin the GPU→software fallback order explicitly (matches the Avalonia default) so a
            // silent drop to CPU software rendering becomes a deliberate, observable choice.
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
            })
            .WithInterFont()
            .LogToTrace();
}
