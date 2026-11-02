using NutriFlow.Api.Contracts;
using NutriFlow.Ui.Components;

namespace NutriFlow.Ui.Tests;

public sealed class ArcGeometryTests
{
    [Fact]
    public void WithoutGoalOnlyTheTrackIsDrawn()
    {
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(500, 30, 20, 50), null);

        Assert.Equal("M 28.16 165.2 A 131.84 131.84 0 0 1 291.84 165.2", arc.Track.Data);
        Assert.Empty(arc.Fill);
        Assert.Empty(arc.Caps);
        Assert.Null(arc.Exceeded);
        Assert.Equal("17.6", arc.StrokeText);
    }

    [Fact]
    public void ProgressIsSplitByMacrosWithRoundedOuterEnds()
    {
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(1000, 50, 30, 120), new NutritionResponse(2000, 0, 0, 0));

        Assert.Equal(["protein", "fat", "carbs"], arc.Fill.Select(path => path.Tone));
        Assert.Equal(["protein", "carbs"], arc.Caps.Select(cap => cap.Tone));
        Assert.All(arc.Fill, path => Assert.StartsWith("M ", path.Data));
        // half of the goal ends straight above the centre
        Assert.Equal(160, arc.Caps[^1].X, 2);
        Assert.Null(arc.Exceeded);
    }

    [Fact]
    public void CaloriesWithoutMacrosFillInTheAccentColour()
    {
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(500, 0, 0, 0), new NutritionResponse(1000, 0, 0, 0));

        ArcPath fill = Assert.Single(arc.Fill);
        Assert.Equal("accent", fill.Tone);
        Assert.Equal(2, arc.Caps.Count);
    }

    [Fact]
    public void OverTheGoalTheArcIsFullAndRingedInDanger()
    {
        ArcGeometry arc = ArcGeometry.Create(new NutritionResponse(2500, 100, 80, 300), new NutritionResponse(2000, 0, 0, 0));

        Assert.NotNull(arc.Exceeded);
        Assert.Equal("danger", arc.Exceeded.Tone);
        Assert.Equal(291.84, arc.Caps[^1].X, 2);
    }
}
