using Microsoft.Maui.Graphics;
using NutriFlow.Api.Contracts;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

internal sealed class NutritionArcView : GraphicsView
{
    public NutritionArcView(NutritionResponse consumed, NutritionResponse? goal)
    {
        AutomationId = "diary-arcs";
        HeightRequest = 210;
        HorizontalOptions = LayoutOptions.Fill;
        InputTransparent = true;
        AutomationProperties.SetIsInAccessibleTree(this, false);
        SetNutrition(consumed, goal);
    }

    public void SetNutrition(NutritionResponse consumed, NutritionResponse? goal)
    {
        ArgumentNullException.ThrowIfNull(consumed);
        Drawable = new NutritionArcDrawable(consumed, goal);
        Invalidate();
    }

    private sealed class NutritionArcDrawable : IDrawable
    {
        private readonly IReadOnlyList<NutritionArcSegment> _consumed;
        private readonly IReadOnlyList<NutritionArcSegment> _goal;

        public NutritionArcDrawable(NutritionResponse consumed, NutritionResponse? goal)
        {
            _consumed = NutritionArcLayout.Create(consumed.ProteinGrams, consumed.FatGrams, consumed.CarbohydratesGrams);
            _goal = goal is null
                ? Array.Empty<NutritionArcSegment>()
                : NutritionArcLayout.Create(goal.ProteinGrams, goal.FatGrams, goal.CarbohydratesGrams);
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
            {
                return;
            }

            float stroke = Math.Min(24, Math.Min(dirtyRect.Width * 0.065f, dirtyRect.Height * 0.11f));
            float padding = Math.Min(6, Math.Min(dirtyRect.Width, dirtyRect.Height) * 0.03f);
            float radius = Math.Min(dirtyRect.Width / 2 - padding - stroke / 2,
                dirtyRect.Height - padding - stroke / 2);
            float innerRadius = radius - stroke * 1.32f;
            if (innerRadius <= stroke / 2)
            {
                return;
            }

            float centerX = dirtyRect.Center.X;
            float centerY = dirtyRect.Bottom - padding - stroke / 2;
            canvas.SaveState();
            canvas.StrokeLineCap = LineCap.Butt;
            canvas.StrokeLineJoin = LineJoin.Round;

            DrawRing(canvas, centerX, centerY, radius, stroke, _goal);
            DrawRing(canvas, centerX, centerY, innerRadius, stroke, _consumed);

            canvas.RestoreState();
        }

        private static void DrawRing(ICanvas canvas, float centerX, float centerY, float radius,
            float stroke, IReadOnlyList<NutritionArcSegment> segments)
        {
            DrawArc(canvas, centerX, centerY, radius, 180, 0, stroke, Ui.SurfaceRaised);
            foreach (NutritionArcSegment segment in segments)
            {
                decimal gap = Math.Min(2m, segment.SweepAngle * 0.15m);
                float start = (float)(segment.StartAngle - gap / 2);
                float end = (float)(segment.StartAngle - segment.SweepAngle + gap / 2);
                Color color = segment.Macro switch
                {
                    NutritionMacro.Protein => Ui.Protein,
                    NutritionMacro.Fat => Ui.Fat,
                    NutritionMacro.Carbohydrates => Ui.Carbs,
                    _ => throw new ArgumentOutOfRangeException(nameof(segment))
                };
                DrawArc(canvas, centerX, centerY, radius, start, end, stroke, color);
            }
        }

        private static void DrawArc(ICanvas canvas, float centerX, float centerY, float radius,
            float startAngle, float endAngle, float stroke, Color color)
        {
            if (startAngle <= endAngle)
            {
                return;
            }

            using PathF path = new();
            int steps = Math.Max(1, (int)Math.Ceiling((startAngle - endAngle) / 2));
            for (int index = 0; index <= steps; index++)
            {
                float angle = (startAngle + (endAngle - startAngle) * index / steps) * MathF.PI / 180;
                float x = centerX + radius * MathF.Cos(angle);
                float y = centerY - radius * MathF.Sin(angle);
                if (index == 0)
                {
                    path.MoveTo(x, y);
                }
                else
                {
                    path.LineTo(x, y);
                }
            }

            canvas.StrokeColor = color;
            canvas.StrokeSize = stroke;
            canvas.DrawPath(path);
        }
    }
}
