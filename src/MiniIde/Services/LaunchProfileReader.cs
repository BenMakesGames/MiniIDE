using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MiniIde.Services;

/// <summary>Reads the launch profiles <c>dotnet run -lp</c> will actually honour for a project. Mirrors
/// <see cref="ProjectClassifier"/>: static, per-project, and deliberately tolerant — a missing, unreadable or
/// malformed file yields no profiles rather than throwing, because launch settings are an optional garnish
/// and an unparseable one must not stop the project from being runnable.</summary>
public static class LaunchProfileReader
{
    /// <summary>The usable profile names for <paramref name="projectPath"/>, in file order (so the first is
    /// the one <c>dotnet run</c> picks when given no <c>-lp</c>). Empty when the project has no launch
    /// settings, and equally empty when it has some but none are usable — the caller treats both the same.</summary>
    public static IReadOnlyList<string> Read(string projectPath)
    {
        var dir = Path.GetDirectoryName(projectPath);
        if (dir is null) return Array.Empty<string>();

        // dotnet's own precedence: Properties/launchSettings.json wins outright, and the file-based-app
        // `<name>.run.json` beside the project is consulted only when that one is absent. Matching the order
        // is what keeps the list we offer equal to the list that would actually apply.
        var settingsPath = Path.Combine(dir, "Properties", "launchSettings.json");
        if (!File.Exists(settingsPath))
            settingsPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(projectPath) + ".run.json");
        if (!File.Exists(settingsPath)) return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(settingsPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (!doc.RootElement.TryGetProperty("profiles", out var profiles) ||
                profiles.ValueKind != JsonValueKind.Object)
                return Array.Empty<string>();

            var names = new List<string>();
            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.ValueKind != JsonValueKind.Object) continue;
                // Every other commandName is rejected *softly*: `dotnet run -lp` prints that the type is not
                // supported and then runs with no profile at all. Offering them unfiltered would put entries
                // in the flyout that silently do nothing.
                if (!profile.Value.TryGetProperty("commandName", out var command)) continue;
                if (command.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(command.GetString(), "Project", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(profile.Name);
            }
            return names;
        }
        catch { return Array.Empty<string>(); }
    }
}
