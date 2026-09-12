namespace MiniIde.Models;

/// <summary>What the Run button is currently pointed at, beyond the project itself. The two axes are
/// orthogonal and neither substitutes for the other: a configuration decides what gets compiled, a profile
/// decides what environment the result launches into.</summary>
/// <param name="Configuration">The build configuration (<c>-c</c>). Only ever a name the solution declared:
/// <c>dotnet run -c &lt;unknown&gt;</c> neither warns nor fails — it builds into a <c>bin</c> folder of that
/// name with <c>DEBUG</c> undefined and runs — so a free-text value would go wrong silently.</param>
/// <param name="LaunchProfile">The launch profile (<c>-lp</c>), or null to let <c>dotnet run</c> pick its own
/// default. Only ever a profile actually found on disk: naming one that does not exist is a hard error,
/// unlike an unknown configuration.</param>
public record RunSelection(string Configuration, string? LaunchProfile);
