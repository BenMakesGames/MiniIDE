namespace MiniIde.ViewModels;

/// <summary>Count formatting for the panels' status lines: "1 error", "3 warnings", "2 matches" — never a
/// parenthetical "(s)".</summary>
internal static class Plural
{
    public static string Of(int count, string noun, string suffix = "s") =>
        $"{count} {noun}{(count == 1 ? "" : suffix)}";
}
