using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MiniIde.Services;

/// <summary>One user's settings for one solution: <c>.miniide/&lt;Solution&gt;.local.json</c> beside the solution
/// file. Personal, so it is kept out of git — by a <c>.gitignore</c> inside <c>.miniide/</c> that MiniIDE owns,
/// never by touching the repository's own. (<c>*.local.*</c> = personal, after <c>.claude/settings.local.json</c>;
/// a shared, committed <c>&lt;Solution&gt;.json</c> can sit beside it if one is ever needed.)</summary>
/// <param name="StarredProjects">Starred projects' paths, relative to the solution directory, so the file stays
/// valid when the checkout moves.</param>
public sealed record LocalSolutionSettings(IReadOnlyList<string> StarredProjects)
{
    public static readonly LocalSolutionSettings Empty = new(Array.Empty<string>());

    public const string FolderName = ".miniide";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathFor(string solutionPath) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(solutionPath))!,
        FolderName,
        Path.GetFileNameWithoutExtension(solutionPath) + ".local.json");

    /// <summary>Reads the settings for <paramref name="solutionPath"/>; <see cref="Empty"/> when there is no file
    /// yet. Throws on a file that exists but can't be read or parsed — the caller must not then save over it, or
    /// a hand-edit with a typo would silently wipe the user's settings.</summary>
    public static LocalSolutionSettings Load(string solutionPath)
    {
        var path = PathFor(solutionPath);
        if (!File.Exists(path)) return Empty;
        var loaded = JsonSerializer.Deserialize<LocalSolutionSettings>(File.ReadAllText(path), Json);
        // A file holding `null` or omitting the list deserializes to nulls; normalize rather than trust it.
        return new LocalSolutionSettings(loaded?.StarredProjects ?? Array.Empty<string>());
    }

    /// <summary>Writes the settings, creating <c>.miniide/</c> (and its <c>.gitignore</c>) on first use. Written
    /// to a temp file and moved into place, so a crash mid-write can't leave a truncated file behind.</summary>
    public void Save(string solutionPath)
    {
        var path = PathFor(solutionPath);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);

        // Only ever created, never rewritten: if the user edits it (say, to commit their stars), that's theirs.
        var gitignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(gitignore))
            File.WriteAllText(gitignore, "# Personal MiniIDE settings; see LocalSolutionSettings.\n*.local.json\n");

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
