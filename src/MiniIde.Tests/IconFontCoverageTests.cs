using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace MiniIde.Tests;

// Guards the icon-font subset: every MDI glyph referenced in source must exist in the shipped,
// subsetted font. If you add an icon (e.g. in ActionIcon/FileIcon) but forget to regenerate the
// font, this fails loudly instead of the glyph silently rendering as a blank tofu box.
//
//   Regenerate:  cd tools && npm run subset
//
// The codepoint scan mirrors tools/subset-icons.mjs on purpose — both treat the "\U000FXXXX"
// escapes in src/MiniIde/**/*.cs as the single source of truth for which glyphs to ship.
public class IconFontCoverageTests
{
    private static readonly Regex CsEscape = new(@"\\U([0-9A-Fa-f]{8})", RegexOptions.Compiled);

    [Fact]
    public void ShippedIconFont_covers_every_referenced_glyph()
    {
        var appDir = Path.Combine(RepoRoot(), "src", "MiniIde");
        var fontPath = Path.Combine(appDir, "Assets", "icons", "MaterialDesignIconsDesktop.ttf");

        File.Exists(fontPath).ShouldBeTrue($"Shipped icon font not found at {fontPath}");

        var referenced = ReferencedCodepoints(appDir);
        referenced.ShouldNotBeEmpty("Expected MDI glyph codepoints (\\U000FXXXX) in src/MiniIde.");

        using var typeface = SKTypeface.FromFile(fontPath);
        typeface.ShouldNotBeNull($"Could not load icon font at {fontPath}");

        var missing = referenced
            .Where(cp => typeface.GetGlyph(cp) == 0)
            .Select(cp => $"U+{cp:X5}")
            .ToArray();

        missing.ShouldBeEmpty(
            $"Icon font is missing {missing.Length} referenced glyph(s): {string.Join(" ", missing)}. " +
            "Regenerate with:  cd tools && npm run subset");
    }

    private static SortedSet<int> ReferencedCodepoints(string appDir)
    {
        var cps = new SortedSet<int>();
        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            foreach (Match m in CsEscape.Matches(File.ReadAllText(file)))
            {
                var cp = Convert.ToInt32(m.Groups[1].Value, 16);
                if (cp is >= 0xF0000 and <= 0xFFFFF) cps.Add(cp); // MDI lives in Supplementary PUA-A
            }
        }
        return cps;
    }

    // src/MiniIde.Tests/IconFontCoverageTests.cs -> repo root
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
