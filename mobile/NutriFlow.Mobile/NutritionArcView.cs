using Microsoft.Maui.Graphics;
using NutriFlow.Api.Contracts;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

internal sealed class NutritionArcView : GraphicsView
{
    public NutritionArcView(NutritionResponse consumed, NutritionResponse? goal)
    {
        AutomationId = "diary-arcs";
        HorizontalOptions = LayoutOptions.Fill;
        InputTransparent = true;
        AutomationProperties.SetIsInAccessibleTree(this, false);
        HeightRequest = 180;
        SetNutrition(consumed, goal);
        // a semicircle is about half as tall as it is wide; the measure pass sees the width before the card padding,
        // so the height follows the arranged width instead of leaving a gap above the arc on narrow phones
        SizeChanged += (_, _) =>
        {
            double height = Math.Clamp(Width * 0.53 + 8, 120, 200);
            if (Width > 0 && Math.Abs(HeightRequest - height) > 1)
            {
                HeightRequest = height;
            }
        };
    }

    public void SetNutrition(NutritionResponse consumed, NutritionResponse? goal)
    {
        ArgumentNullException.ThrowIfNull(consumed);
        Drawable = new NutritionArcDrawable(consumed, goal);
        Invalidate();
    }

    private sealed class NutritionArcDrawable : IDrawable
    {
        private readonly IReadOnlyList<NutritionArcSegment> _segments;
        private readonly float _progress;
        private readonly bool _exceeded;

        public NutritionArcDrawable(NutritionResponse consumed, NutritionResponse? goal)
        {
            decimal goalCalories = goal?.Calories ?? 0m;
            _segments = NutritionArcLayout.CreateProgress(consumed.Calories, goalCalories,
                consumed.ProteinGrams, consumed.FatGrams, consumed.CarbohydratesGrams);
            _progress = (float)NutritionArcLayout.Progress(consumed.Calories, goalCalories);
            _exceeded = goalCalories > 0m && consumed.Calories > goalCalories;
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
            {
                return;
            }

            float stroke = Math.Min(20, dirtyRect.Width * 0.055f);
            // the outer over-goal ring needs room beyond the main stroke on every screen width
            float radius = Math.Min(dirtyRect.Width / 2 - stroke * 1.6f, dirtyRect.Height - stroke * 1.6f);
            if (radius <= stroke)
            {
                return;
            }

            float centerX = dirtyRect.Center.X;
            float centerY = dirtyRect.Bottom - stroke / 2 - 2;
            canvas.SaveState();
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.StrokeLineCap = LineCap.Round;
            DrawArc(canvas, centerX, centerY, radius, 180, 0, stroke, Ui.SurfacePressed);

            canvas.StrokeLineCap = LineCap.Butt;
            if (_segments.Count == 0 && _progress > 0)
            {
                DrawArc(canvas, centerX, centerY, radius, 180, 180 - 180 * _progress, stroke, Ui.Accent);
                DrawCap(canvas, centerX, centerY, radius, 180, stroke, Ui.Accent);
                DrawCap(canvas, centerX, centerY, radius, 180 - 180 * _progress, stroke, Ui.Accent);
            }

            for (int index = 0; index < _segments.Count; index++)
            {
                NutritionArcSegment segment = _segments[index];
                float gap = index < _segments.Count - 1 ? Math.Min(1.6f, (float)segment.SweepAngle * 0.3f) : 0;
                Color color = segment.Macro switch
                {
                    NutritionMacro.Protein => Ui.Protein,
                    NutritionMacro.Fat => Ui.Fat,
                    NutritionMacro.Carbohydrates => Ui.Carbs,
                    _ => throw new ArgumentOutOfRangeException(nameof(segment))
                };
                float start = (float)segment.StartAngle;
                float end = start - (float)segment.SweepAngle;
                DrawArc(canvas, centerX, centerY, radius, start, end + gap, stroke, color);
                // rounded outer ends match the rounded track instead of cutting it square
                if (index == 0)
                {
                    DrawCap(canvas, centerX, centerY, radius, start, stroke, color);
                }

                if (index == _segments.Count - 1)
                {
                    DrawCap(canvas, centerX, centerY, radius, end, stroke, color);
                }
            }

            if (_exceeded)
            {
                canvas.StrokeLineCap = LineCap.Round;
                DrawArc(canvas, centerX, centerY, radius + stroke * 0.95f, 180, 0, 3, Ui.Danger);
            }

            canvas.RestoreState();
        }

        private static void DrawCap(ICanvas canvas, float centerX, float centerY, float radius, float angle, float stroke, Color color)
        {
            float radians = angle * MathF.PI / 180;
            canvas.FillColor = color;
            canvas.FillCircle(centerX + radius * MathF.Cos(radians), centerY - radius * MathF.Sin(radians), stroke / 2);
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
