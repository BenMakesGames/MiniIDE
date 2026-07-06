namespace BenMakesGames.FileGrepper;

/// <summary>A single match. <paramref name="Line"/> and <paramref name="Column"/> are 1-based.</summary>
public sealed record GrepHit(string File, int Line, int Column, string Preview);
