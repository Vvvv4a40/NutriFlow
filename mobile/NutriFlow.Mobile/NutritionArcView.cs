using Microsoft.Maui.Graphics;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

internal sealed class NutritionArcView : GraphicsView
{
    public NutritionArcView(NutritionResponse consumed, NutritionResponse? goal)
    {
        HeightRequest = 190;
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

    private sealed class NutritionArcDrawable(NutritionResponse consumed, NutritionResponse? goal) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
            {
                return;
            }

            float stroke = Math.Clamp(dirtyRect.Width * 0.082f, 18, 30);
            float radius = Math.Min(dirtyRect.Width / 2 - stroke / 2 - 14,
                dirtyRect.Height - stroke - 16);
            if (radius <= stroke)
            {
                return;
            }

            float centerX = dirtyRect.Center.X;
            float centerY = dirtyRect.Bottom - stroke / 2 - 6;
            canvas.SaveState();
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;

            DrawSegment(canvas, centerX, centerY, radius, stroke, 180, Ui.Protein,
                Progress(consumed.ProteinGrams, goal?.ProteinGrams));
            DrawSegment(canvas, centerX, centerY, radius, stroke, 120, Ui.Fat,
                Progress(consumed.FatGrams, goal?.FatGrams));
            DrawSegment(canvas, centerX, centerY, radius, stroke, 60, Ui.Carbs,
                Progress(consumed.CarbohydratesGrams, goal?.CarbohydratesGrams));

            canvas.RestoreState();
        }

        private void DrawSegment(ICanvas canvas, float centerX, float centerY, float radius,
            float stroke, float startAngle, Color color, float progress)
        {
            const float gap = 14;
            float start = startAngle - gap / 2;
            float end = startAngle - 60 + gap / 2;
            Color track = goal is null ? Ui.SurfaceRaised : color.WithAlpha(0.18f);
            DrawArc(canvas, centerX, centerY, radius, start, end, stroke, track);
            if (progress > 0)
            {
                DrawArc(canvas, centerX, centerY, radius, start,
                    start - (start - end) * progress, stroke, color);
            }
        }

        private static float Progress(decimal value, decimal? target)
        {
            if (target is null or <= 0 || value <= 0)
            {
                return 0;
            }

            return value >= target.Value ? 1 : (float)(value / target.Value);
        }

        private static void DrawArc(ICanvas canvas, float centerX, float centerY, float radius,
            float startAngle, float endAngle, float stroke, Color color)
        {
            using PathF path = new();
            int steps = Math.Max(2, (int)Math.Ceiling((startAngle - endAngle) / 2));
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
