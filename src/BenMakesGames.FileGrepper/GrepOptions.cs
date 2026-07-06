namespace BenMakesGames.FileGrepper;

/// <summary>
/// Grep tuning. The engine has no baked-in prune list — the caller owns all exclusions
/// via <paramref name="SkipDirectory"/> / <paramref name="SkipFile"/>, which receive full paths
/// and return true to exclude.
/// </summary>
public sealed record GrepOptions(
    bool Regex = false,
    bool CaseSensitive = true,
    Predicate<string>? SkipDirectory = null,
    Predicate<string>? SkipFile = null);
