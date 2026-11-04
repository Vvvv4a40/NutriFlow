using System.Globalization;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Ui.Components;

// svg geometry of the diary arc on a fixed 320 × 176 canvas. The goal is the underlay: one zone per macro, as long as its
// share of the energy of the goal. What was eaten fills each macro in its own zone, so macros never run into each other
// and an empty stretch shows what the day still lacks. The filled length of all zones together follows the calories
internal sealed record ArcGeometry(
    double Stroke,
    ArcPath Track,
    IReadOnlyList<ArcPath> Zones,
    IReadOnlyList<ArcCap> ZoneCaps,
    IReadOnlyList<ArcPath> Fill,
    IReadOnlyList<ArcCap> FillCaps,
    IReadOnlyList<ArcPath> Exceeded)
{
    public const double CanvasWidth = 320;
    public const double CanvasHeight = 176;
    private const double ZoneGap = 2.4;
    // a little eaten is still visible, and it is never drawn wider than its own zone
    private const double MinimumFill = 2.4;

    public string StrokeText => Math.Round(Stroke, 2).ToString(CultureInfo.InvariantCulture);

    public static ArcGeometry Create(NutritionResponse consumed, NutritionResponse? goal)
    {
        ArgumentNullException.ThrowIfNull(consumed);
        const double stroke = 22;
        // the outer over-goal ring needs room beyond the main stroke
        double radius = Math.Min(CanvasWidth / 2 - stroke * 1.6, CanvasHeight - stroke * 1.6);
        Circle circle = new(CanvasWidth / 2, CanvasHeight - stroke / 2 - 2, radius);
        Circle ring = circle with { Radius = radius + stroke * 0.8 };
        ArcPath track = new(circle.Path(180, 0), "track");
        List<ArcPath> zones = [];
        List<ArcCap> zoneCaps = [];
        List<ArcPath> fill = [];
        List<ArcCap> fillCaps = [];
        List<ArcPath> exceeded = [];
        if (goal is null || goal.Calories <= 0m)
        {
            return new ArcGeometry(stroke, track, zones, zoneCaps, fill, fillCaps, exceeded);
        }

        bool overCalories = consumed.Calories > goal.Calories;
        MacroZone[] macros =
        [
            new("protein", consumed.ProteinGrams, goal.ProteinGrams, goal.ProteinGrams * 4m),
            new("fat", consumed.FatGrams, goal.FatGrams, goal.FatGrams * 9m),
            new("carbs", consumed.CarbohydratesGrams, goal.CarbohydratesGrams, goal.CarbohydratesGrams * 4m)
        ];
        MacroZone[] present = macros.Where(macro => macro.Energy > 0m).ToArray();
        decimal energy = present.Sum(macro => macro.Energy);
        if (present.Length == 0)
        {
            // a goal of calories alone has no macros to compare with
            double end = 180 - 180 * Math.Min(1, (double)(consumed.Calories / goal.Calories));
            if (consumed.Calories > 0m)
            {
                fill.Add(new ArcPath(circle.Path(180, end), "accent"));
                fillCaps.Add(circle.Cap(180, stroke, "accent"));
                fillCaps.Add(circle.Cap(end, stroke, "accent"));
            }
        }

        double available = 180 - ZoneGap * (present.Length - 1);
        double start = 180;
        for (int index = 0; index < present.Length; index++)
        {
            MacroZone macro = present[index];
            double sweep = available * (double)(macro.Energy / energy);
            double zoneEnd = index == present.Length - 1 ? 0 : start - sweep;
            zones.Add(new ArcPath(circle.Path(start, zoneEnd), macro.Tone));
            if (index == 0)
            {
                zoneCaps.Add(circle.Cap(180, stroke, macro.Tone));
            }

            if (index == present.Length - 1)
            {
                zoneCaps.Add(circle.Cap(0, stroke, macro.Tone));
            }

            if (macro.Eaten > 0m)
            {
                double fraction = Math.Min(1, (double)(macro.Eaten / macro.Goal));
                double filled = Math.Min(sweep, Math.Max(MinimumFill, sweep * fraction));
                double fillEnd = fraction >= 1 ? zoneEnd : start - filled;
                fill.Add(new ArcPath(circle.Path(start, fillEnd), macro.Tone));
                if (index == 0)
                {
                    fillCaps.Add(circle.Cap(180, stroke, macro.Tone));
                }

                if (index == present.Length - 1 && fraction >= 1)
                {
                    fillCaps.Add(circle.Cap(0, stroke, macro.Tone));
                }

                if (!overCalories && macro.Eaten > macro.Goal)
                {
                    exceeded.Add(new ArcPath(ring.Path(start, zoneEnd), "danger"));
                }
            }

            start = zoneEnd - ZoneGap;
        }

        if (overCalories)
        {
            exceeded.Add(new ArcPath(ring.Path(180, 0), "danger"));
        }

        return new ArcGeometry(stroke, track, zones, zoneCaps, fill, fillCaps, exceeded);
    }

    private sealed record MacroZone(string Tone, decimal Eaten, decimal Goal, decimal Energy);

    private sealed record Circle(double CenterX, double CenterY, double Radius)
    {
        // angles in degrees counter-clockwise from three o'clock; the arc runs over the top
        public string Path(double startAngle, double endAngle)
        {
            (double startX, double startY) = Point(startAngle);
            (double endX, double endY) = Point(endAngle);
            int largeArc = startAngle - endAngle > 180 ? 1 : 0;
            return FormattableString.Invariant(
                $"M {Round(startX)} {Round(startY)} A {Round(Radius)} {Round(Radius)} 0 {largeArc} 1 {Round(endX)} {Round(endY)}");
        }

        public ArcCap Cap(double angle, double stroke, string tone)
        {
            (double x, double y) = Point(angle);
            return new ArcCap(Round(x), Round(y), Round(stroke / 2), tone);
        }

        private (double X, double Y) Point(double angle)
        {
            double radians = angle * Math.PI / 180;
            return (CenterX + Radius * Math.Cos(radians), CenterY - Radius * Math.Sin(radians));
        }
    }

    private static double Round(double value) => Math.Round(value, 2);
}

internal sealed record ArcPath(string Data, string Tone);

// numbers reach svg attributes as invariant text: the ru-RU culture of the app would write a decimal comma
internal sealed record ArcCap(double X, double Y, double Radius, string Tone)
{
    public string XText => X.ToString(CultureInfo.InvariantCulture);
    public string YText => Y.ToString(CultureInfo.InvariantCulture);
    public string RadiusText => Radius.ToString(CultureInfo.InvariantCulture);
}
