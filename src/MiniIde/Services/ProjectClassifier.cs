using System;
using System.Xml.Linq;
using MiniIde.Models;

namespace MiniIde.Services;

public static class ProjectClassifier
{
    public static ProjectKind Classify(string csprojPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(csprojPath); }
        catch { return ProjectKind.Lib; }

        var root = doc.Root;
        if (root is null) return ProjectKind.Lib;

        var sdk = (string?)root.Attribute("Sdk") ?? "";

        bool hasTestSdk = false;
        bool isTestFlag = false;
        string? outputType = null;

        foreach (var el in root.Descendants())
        {
            var name = el.Name.LocalName;
            if (name.Equals("PackageReference", StringComparison.OrdinalIgnoreCase))
            {
                var inc = (string?)el.Attribute("Include");
                if (inc is not null && inc.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                    hasTestSdk = true;
            }
            else if (name.Equals("IsTestProject", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(el.Value, out var b) && b) isTestFlag = true;
            }
            else if (name.Equals("OutputType", StringComparison.OrdinalIgnoreCase))
            {
                outputType ??= el.Value;
            }
        }

        if (hasTestSdk || isTestFlag) return ProjectKind.Tst;
        if (sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)) return ProjectKind.Web;
        if (outputType is not null && (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)))
            return ProjectKind.Exe;
        return ProjectKind.Lib;
    }
}
