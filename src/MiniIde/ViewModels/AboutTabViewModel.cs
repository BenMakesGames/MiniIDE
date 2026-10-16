using System;
using System.Reflection;

namespace MiniIde.ViewModels;

/// <summary>The About tab: name, version, author, and where to find more. Static content — one per app.</summary>
public sealed class AboutTabViewModel : TabViewModelBase
{
    public const string Id = "about:";

    public static readonly Uri SourceCodeUri = new("https://github.com/BenMakesGames/MiniIDE");
    public static readonly Uri GamesUri = new("https://benmakesgames.com");
    public static readonly Uri PatreonUri = new("https://www.patreon.com/c/BenMakesGames");

    public AboutTabViewModel() : base(Id, filePath: null) { }

    public override string Header => "About";

    public string Title => $"MiniIDE {Version}";

    /// <summary>The informational version minus the SDK's "+commit" suffix, which is noise to a reader.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(AboutTabViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null)
        {
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }
        return assembly.GetName().Version?.ToString(3) ?? "(unknown version)";
    }
}
