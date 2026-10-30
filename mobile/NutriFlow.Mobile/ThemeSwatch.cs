namespace NutriFlow.Mobile;

// a circle split between the two colours of a theme pair; the selected one gets an outer ring
internal sealed class ThemeSwatch : GraphicsView
{
    public ThemeSwatch(Theme theme, bool selected, double size = 56)
    {
        WidthRequest = size;
        HeightRequest = size;
        InputTransparent = true;
        AutomationProperties.SetIsInAccessibleTree(this, false);
        Drawable = new SwatchDrawable(Color.FromArgb(theme.SwatchBase), Color.FromArgb(theme.SwatchAccent), selected,
            Ui.TextPrimary);
    }

    private sealed class SwatchDrawable(Color baseColor, Color accent, bool selected, Color ring) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            float size = Math.Min(dirtyRect.Width, dirtyRect.Height);
            float centerX = dirtyRect.Center.X;
            float centerY = dirtyRect.Center.Y;
            float radius = size / 2 - 7;
            canvas.SaveState();
            canvas.FillColor = baseColor;
            canvas.FillCircle(centerX, centerY, radius);

            // the lower-right half, from 45° clockwise to 225°, closed through the centre
            using PathF half = new();
            for (int degrees = 45; degrees >= -135; degrees -= 5)
            {
                float angle = degrees * MathF.PI / 180;
                float x = centerX + radius * MathF.Cos(angle);
                float y = centerY - radius * MathF.Sin(angle);
                if (degrees == 45)
                {
                    half.MoveTo(x, y);
                }
                else
                {
                    half.LineTo(x, y);
                }
            }

            half.Close();
            canvas.FillColor = accent;
            canvas.FillPath(half);

            // a swatch in the colour of the current card would melt into it without an outline
            canvas.StrokeColor = ring.WithAlpha(0.35f);
            canvas.StrokeSize = 1;
            canvas.DrawCircle(centerX, centerY, radius);
            if (selected)
            {
                canvas.StrokeColor = ring;
                canvas.StrokeSize = 2.5f;
                canvas.DrawCircle(centerX, centerY, size / 2 - 1.5f);
            }

            canvas.RestoreState();
        }
    }
}
