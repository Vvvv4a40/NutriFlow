using NutriFlow.Api.Contracts;
using NutriFlow.Ui.Components;

namespace NutriFlow.Ui.Tests;

public sealed class ArcGeometryTests
{
    private static readonly NutritionResponse Goal = new(2000, 150, 67, 200);

    [Fact]
    public void WithoutGoalOnlyTheTrackIsDrawn()
    {
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(500, 30, 20, 50), null);

        Assert.Equal("M 35.2 163 A 124.8 124.8 0 0 1 284.8 163", arc.Track.Data);
        Assert.Empty(arc.Zones);
        Assert.Empty(arc.Fill);
        Assert.Empty(arc.Exceeded);
        Assert.Equal("22", arc.StrokeText);
    }

    [Fact]
    public void TheGoalIsSplitIntoZonesAcrossTheWholeArc()
    {
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(0, 0, 0, 0), Goal);

        Assert.Equal(["protein", "fat", "carbs"], arc.Zones.Select(zone => zone.Tone));
        Assert.StartsWith("M 35.2 163 ", arc.Zones[0].Data);
        Assert.EndsWith(" 284.8 163", arc.Zones[^1].Data);
        Assert.Equal(["protein", "carbs"], arc.ZoneCaps.Select(cap => cap.Tone));
        Assert.Empty(arc.Fill);
    }

    [Fact]
    public void EveryMacroFillsOnlyItsOwnZone()
    {
        // the day from the acceptance photo: small amounts used to pile up on each other at the start of the arc
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(144, 2, 4, 5), Goal);

        Assert.Equal(["protein", "fat", "carbs"], arc.Fill.Select(path => path.Tone));
        for (int index = 0; index < arc.Fill.Count; index++)
        {
            Assert.Equal(StartOf(arc.Zones[index]), StartOf(arc.Fill[index]));
            Assert.True(arc.Fill[index].Data != arc.Zones[index].Data);
        }

        Assert.Empty(arc.Exceeded);
    }

    [Fact]
    public void AFullMacroCoversItsZoneAndAnExcessIsRingedOverIt()
    {
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(1500, 200, 30, 100), Goal);

        Assert.Equal(arc.Zones[0].Data, arc.Fill[0].Data);
        ArcPath ring = Assert.Single(arc.Exceeded);
        Assert.Equal("danger", ring.Tone);
    }

    [Fact]
    public void OverTheGoalTheWholeArcIsRinged()
    {
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(2500, 200, 100, 300), Goal);

        ArcPath ring = Assert.Single(arc.Exceeded);
        Assert.EndsWith("0 0 1 302.4 163", ring.Data);
    }

    [Fact]
    public void AGoalOfCaloriesAloneFillsInTheAccentColour()
    {
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(500, 0, 0, 0), new NutritionResponse(1000, 0, 0, 0));

        Assert.Empty(arc.Zones);
        ArcPath fill = Assert.Single(arc.Fill);
        Assert.Equal("accent", fill.Tone);
        Assert.Equal(2, arc.FillCaps.Count);
        // half of the goal ends straight above the centre
        Assert.Equal(160, arc.FillCaps[^1].X, 2);
    }

    private static string StartOf(ArcPath path) => path.Data[..path.Data.IndexOf(" A ", StringComparison.Ordinal)];
}
