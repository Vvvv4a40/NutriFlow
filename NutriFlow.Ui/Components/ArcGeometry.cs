using System.Globalization;
using NutriFlow.Api.Contracts;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Ui.Components;

// svg geometry of the diary arc; the same drawing as NutritionArcView of the android app on a fixed 320 × 176 canvas
internal sealed record ArcGeometry(
    double Width,
    double Height,
    double Stroke,
    ArcPath Track,
    IReadOnlyList<ArcPath> Fill,
    IReadOnlyList<ArcCap> Caps,
    ArcPath? Exceeded)
{
    public const double CanvasWidth = 320;
    public const double CanvasHeight = 176;

    public string StrokeText => Math.Round(Stroke, 2).ToString(CultureInfo.InvariantCulture);

    public static ArcGeometry Create(NutritionResponse consumed, NutritionResponse? goal)
    {
        ArgumentNullException.ThrowIfNull(consumed);
        decimal goalCalories = goal?.Calories ?? 0m;
        IReadOnlyList<NutritionArcSegment> segments = NutritionArcLayout.CreateProgress(consumed.Calories, goalCalories,
            consumed.ProteinGrams, consumed.FatGrams, consumed.CarbohydratesGrams);
        double progress = (double)NutritionArcLayout.Progress(consumed.Calories, goalCalories);
        bool exceeded = goalCalories > 0m && consumed.Calories > goalCalories;

        double stroke = Math.Min(20, CanvasWidth * 0.055);
        // the outer over-goal ring needs room beyond the main stroke
        double radius = Math.Min(CanvasWidth / 2 - stroke * 1.6, CanvasHeight - stroke * 1.6);
        Circle circle = new(CanvasWidth / 2, CanvasHeight - stroke / 2 - 2, radius);

        List<ArcPath> fill = [];
        List<ArcCap> caps = [];
        if (segments.Count == 0 && progress > 0)
        {
            double end = 180 - 180 * progress;
            fill.Add(new ArcPath(circle.Path(180, end), "accent"));
            caps.Add(circle.Cap(180, stroke, "accent"));
            caps.Add(circle.Cap(end, stroke, "accent"));
        }

        for (int index = 0; index < segments.Count; index++)
        {
            NutritionArcSegment segment = segments[index];
            string tone = segment.Macro switch
            {
                NutritionMacro.Protein => "protein",
                NutritionMacro.Fat => "fat",
                NutritionMacro.Carbohydrates => "carbs",
                _ => throw new ArgumentOutOfRangeException(nameof(consumed))
            };
            double start = (double)segment.StartAngle;
            double end = start - (double)segment.SweepAngle;
            double gap = index < segments.Count - 1 ? Math.Min(1.6, (double)segment.SweepAngle * 0.3) : 0;
            fill.Add(new ArcPath(circle.Path(start, end + gap), tone));
            // rounded outer ends match the rounded track instead of cutting it square
            if (index == 0)
            {
                caps.Add(circle.Cap(start, stroke, tone));
            }

            if (index == segments.Count - 1)
            {
                caps.Add(circle.Cap(end, stroke, tone));
            }
        }

        ArcPath? exceededRing = exceeded
            ? new ArcPath((circle with { Radius = radius + stroke * 0.95 }).Path(180, 0), "danger")
            : null;
        return new ArcGeometry(CanvasWidth, CanvasHeight, stroke, new ArcPath(circle.Path(180, 0), "track"), fill, caps, exceededRing);
    }

    private sealed record Circle(double CenterX, double CenterY, double Radius)
    {
        // angles in degrees counter-clockwise from three o'clock, as in the android drawing; the arc runs over the top
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
