using MiniIde.Services;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>Drives the real Roslyn classifier over a snippet and asks the real eligibility rule whether the
/// context menu should offer Find-usages / Go-to-definition at a given token.
///
/// <para>The case that motivated these: a <c>static</c> field is classified <b>twice</b> — "field name" AND
/// the additive "static symbol". A rule that inspects only the first covering span decides eligibility on
/// whichever Roslyn happens to return first, so <c>private static EngineHost _active</c> greyed the menu out.
/// <see cref="StaticField_IsEligible"/> is the regression guard.</para></summary>
public sealed class SymbolClassificationTests
{
    // NOTE: EngineHost is deliberately NOT defined here. SyntaxHighlightService classifies each file in
    // isolation (an AdhocWorkspace referencing only mscorlib), so types from elsewhere in the real project do
    // not resolve — and Roslyn's span ORDER depends on that. With the type resolvable, "field name" comes back
    // first; without it, the additive "static symbol" does. Defining EngineHost here would quietly reproduce
    // the wrong configuration and let a first-span-only rule pass.
    private const string Source = """
        public static class Holder
        {
            private static EngineHost _active = null!;
            private int _instanceField;
            public const int Max = 3;
            public static void Use(string name) => _active.ToString();
            // a comment mentioning _active
        }
        """;

    private readonly SyntaxHighlightService _classifier = new();

    /// <summary>Every classification covering the first occurrence of <paramref name="token"/> (optionally the
    /// occurrence after <paramref name="after"/>), from the real classifier.</summary>
    private async Task<List<string>> ClassificationsAt(string token, string? after = null)
    {
        var from = after is null ? 0 : Source.IndexOf(after, StringComparison.Ordinal);
        var offset = Source.IndexOf(token, from, StringComparison.Ordinal);
        offset.ShouldBeGreaterThanOrEqualTo(0, $"fixture has no '{token}'");

        var spans = await _classifier.ClassifyAsync(Source, TestContext.Current.CancellationToken);
        return spans
            .Where(s => offset >= s.TextSpan.Start && offset < s.TextSpan.End)
            .Select(s => s.ClassificationType)
            .ToList();
    }

    /// <summary>Whether the menu would enable symbol actions there, using the same rule the UI uses.</summary>
    private async Task<bool> EligibleAt(string token, string? after = null) =>
        SymbolClassifications.AllowSymbolActions(await ClassificationsAt(token, after));

    [Fact]
    public async Task StaticField_CarriesBothASemanticAndAnAdditiveClassification()
    {
        // This is *why* the rule must look at every covering span. Roslyn reports the additive "static symbol"
        // ahead of "field name" here, so a rule that inspected only the first span would see a classification
        // that names no symbol and grey the menu out on every static field.
        var covering = await ClassificationsAt("_active");

        covering.ShouldContain("field name");
        covering.ShouldContain("static symbol");
        covering[0].ShouldBe("static symbol", "if this ever changes, the ordering was never the contract");
    }

    [Fact]
    public async Task StaticField_IsEligible() =>
        (await EligibleAt("_active")).ShouldBeTrue(
            "a static field is classified both 'field name' and the additive 'static symbol'");

    [Fact]
    public async Task InstanceField_IsEligible() => (await EligibleAt("_instanceField")).ShouldBeTrue();

    [Fact]
    public async Task TypeName_IsEligible() => (await EligibleAt("EngineHost")).ShouldBeTrue();

    [Fact]
    public async Task MethodName_IsEligible() => (await EligibleAt("Use")).ShouldBeTrue();

    [Fact]
    public async Task Parameter_IsEligible() => (await EligibleAt("name")).ShouldBeTrue();

    [Fact]
    public async Task Constant_IsEligible() => (await EligibleAt("Max")).ShouldBeTrue();

    [Fact]
    public async Task Keyword_IsNotEligible() => (await EligibleAt("private")).ShouldBeFalse();

    [Fact]
    public async Task NullLiteral_IsNotEligible() => (await EligibleAt("null")).ShouldBeFalse();

    [Fact]
    public async Task NumericLiteral_IsNotEligible() => (await EligibleAt("3")).ShouldBeFalse();

    [Fact]
    public async Task InsideAComment_IsNotEligible() =>
        // The '_active' inside the trailing comment, not the field declaration.
        (await EligibleAt("_active", after: "// a comment")).ShouldBeFalse();

    [Fact]
    public void UnclassifiedDocument_IsEligible() =>
        // No covering spans means the classifier hasn't run yet (file just opened). Allow it rather than
        // greying out the menu; the query itself reports "No symbol found" cheaply.
        SymbolClassifications.AllowSymbolActions([]).ShouldBeTrue();
}
