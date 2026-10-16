using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace MiniIde.Tests;

/// <summary>Registers MSBuild once, before any test code runs. Per-class <c>if (!IsRegistered) RegisterDefaults()</c>
/// races when xUnit runs those classes in parallel: the loser throws "MSBuild assemblies were already loaded".</summary>
internal static class MSBuildRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }
}
