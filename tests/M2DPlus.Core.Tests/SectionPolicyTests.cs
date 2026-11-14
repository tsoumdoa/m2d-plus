using M2DPlus.Core.Sections;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class SectionPolicyTests
{
    [Fact]
    public void NestedHolesAndIslandsHaveAlternatingMaterialDepth()
    {
        var nesting = RegionTopology.Nest(new[] { 16d, 9, 4, 1, 2 }, (a, b) => a < b && b < 4);
        Assert.Equal(new[] { 0, 1, 2, 3, 0 }, nesting.Select(n => n.Depth));
        Assert.Equal(new[] { false, true, false, true, false }, nesting.Select(n => n.IsHole));
        Assert.Equal(1, nesting[2].Parent);
    }

    [Fact]
    public void InvalidAreaOrContainmentIsRejected()
    {
        Assert.Throws<ArgumentException>(() => RegionTopology.Nest(new[] { 0d }, (_, _) => false));
        Assert.Throws<ArgumentException>(() => RegionTopology.Nest(new[] { 1d, 2 }, (_, _) => true));
    }

    [Theory]
    [InlineData(-2, 2, SectionStatus.Crossing)]
    [InlineData(-.5, 2, SectionStatus.Uncertain)]
    [InlineData(-2, .5, SectionStatus.Uncertain)]
    [InlineData(.5, 2, SectionStatus.Uncertain)]
    [InlineData(-2, -.5, SectionStatus.Uncertain)]
    [InlineData(2, 3, SectionStatus.InFront)]
    [InlineData(-3, -2, SectionStatus.Behind)]
    [InlineData(0, 2, SectionStatus.Contact)]
    [InlineData(-2, 0, SectionStatus.Contact)]
    public void ContactPolicyUsesOriginalPlaneAndExplicitBand(double min, double max, SectionStatus expected) =>
        Assert.Equal(expected, SectionPolicy.Classify(min, max, 1, true, 1e-12));

    [Fact]
    public void NoncertifiedExtremumCannotClaimExactContact() =>
        Assert.Equal(SectionStatus.Uncertain, SectionPolicy.Classify(0, 2, 1, false, 1e-12));

    [Fact]
    public void ColourSentinelAndViewportBackgroundUseObjectColour()
    {
        var raw = SectionStyleSnapshot.Default(0xff123456, 0xff654321) with { HasStyle = true,
            PatternColour = 0x00ffffff, PatternPrintColour = 0, Background = BackgroundMode.Viewport };
        var resolved = SectionPolicy.EffectiveStyle(raw, HatchMode.Styles);
        Assert.Equal(0xff123456u, resolved.PatternColour);
        Assert.Equal(0xff654321u, resolved.PatternPrintColour);
        Assert.Equal(BackgroundMode.SolidColour, resolved.Background);
        Assert.Equal(0xff123456u, resolved.BackgroundColour);
        Assert.Equal(0x00ffffffu, raw.PatternColour);
    }

    [Fact]
    public void MissingStyleAndExplicitNoFillAreDifferent()
    {
        var plain = SectionStyleSnapshot.Default();
        Assert.True(SectionPolicy.EffectiveStyle(plain, HatchMode.Styles).Pattern!.Solid);
        Assert.Null(SectionPolicy.EffectiveStyle(plain with { HasStyle = true }, HatchMode.Styles).Pattern);
        Assert.Null(SectionPolicy.EffectiveStyle(plain, HatchMode.None).Pattern);
        Assert.Equal(0xff000000u, SectionPolicy.EffectiveStyle(plain, HatchMode.Black).PatternColour);
    }

    [Fact]
    public void MergeComparesDefinitionAndAllPresentationPoliciesByValue()
    {
        var a = SectionPolicy.EffectiveStyle(SectionStyleSnapshot.Default(), HatchMode.Styles) with {
            Pattern = new("A", false, new[] { new PatternLine(.2, default, new(0, .1), new[] { .2, -.1 }) }) };
        var b = a with { Pattern = new("Renamed", false, new[] { new PatternLine(.2, default, new(0, .1), new[] { .2, -.1 }) }),
            Boundary = a.Boundary with { LinetypeSegments = Array.Empty<double>() } };
        Assert.True(SectionPolicy.Compatible(a, b));
        var changes = new[] { b with { Scale = 2 }, b with { Rotation = .1 }, b with { PatternColour = 0xff123456 },
            b with { PatternPrintColour = 0xff123456 }, b with { PatternOrigin = new(1, 0) },
            b with { Background = BackgroundMode.SolidColour }, b with { BackgroundColour = 0xff123456 },
            b with { BackgroundPrintColour = 0xff123456 }, b with { Boundary = b.Boundary with { Visible = false } },
            b with { Boundary = b.Boundary with { WidthScale = 2 } }, b with { Boundary = b.Boundary with { Colour = 0xff123456 } },
            b with { Boundary = b.Boundary with { PrintColour = 0xff123456 } },
            b with { Boundary = b.Boundary with { LinetypeSegments = new[] { 1d, -1 } } },
            b with { Pattern = b.Pattern! with { Lines = new[] { new PatternLine(.3, default, new(0, .1), new[] { .2, -.1 }) } } } };
        Assert.All(changes, c => Assert.False(SectionPolicy.Compatible(a, c)));
    }

    [Fact]
    public void MembershipOrderIsStableAndUnambiguous()
    {
        Assert.Equal(SectionPolicy.MembershipKey(new[] { "b", "a" }), SectionPolicy.MembershipKey(new[] { "a", "b" }));
        Assert.NotEqual(SectionPolicy.MembershipKey(new[] { "a|b", "c" }), SectionPolicy.MembershipKey(new[] { "a", "b|c" }));
    }
}
