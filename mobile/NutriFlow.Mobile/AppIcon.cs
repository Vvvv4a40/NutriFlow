using Microsoft.Maui.Graphics;

namespace NutriFlow.Mobile;

internal enum AppIconKind
{
    Diary,
    Products,
    Dishes,
    Settings,
    Previous,
    Next,
    Target,
    More,
    Microphone
}

internal sealed class AppIcon : GraphicsView
{
    public AppIcon(AppIconKind kind, Color color, double size = 24)
    {
        WidthRequest = size;
        HeightRequest = size;
        InputTransparent = true;
        AutomationProperties.SetIsInAccessibleTree(this, false);
        Drawable = new IconDrawable(kind, color);
    }

    private sealed class IconDrawable(AppIconKind kind, Color color) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();
            canvas.Translate(dirtyRect.X, dirtyRect.Y);
            canvas.Scale(dirtyRect.Width / 24, dirtyRect.Height / 24);
            canvas.StrokeColor = color;
            canvas.FillColor = color;
            canvas.StrokeSize = 1.8f;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;
            switch (kind)
            {
                case AppIconKind.Diary:
                    canvas.DrawRoundedRectangle(5, 4, 14, 17, 3);
                    canvas.DrawLine(9, 2, 9, 6);
                    canvas.DrawLine(15, 2, 15, 6);
                    canvas.DrawLine(9, 11, 15, 11);
                    canvas.DrawLine(9, 15, 13, 15);
                    break;
                case AppIconKind.Products:
                    canvas.DrawRoundedRectangle(4, 5, 16, 15, 3);
                    canvas.DrawLine(4, 10, 20, 10);
                    canvas.DrawLine(9, 5, 9, 20);
                    canvas.DrawLine(15, 5, 15, 20);
                    break;
                case AppIconKind.Dishes:
                    canvas.DrawLine(3, 12, 21, 12);
                    canvas.DrawLine(4, 13, 7, 19);
                    canvas.DrawLine(7, 19, 17, 19);
                    canvas.DrawLine(17, 19, 20, 13);
                    canvas.DrawLine(8, 8, 9, 4);
                    canvas.DrawLine(12, 8, 13, 3);
                    canvas.DrawLine(16, 8, 17, 4);
                    break;
                case AppIconKind.Settings:
                    canvas.DrawLine(4, 7, 20, 7);
                    canvas.DrawLine(4, 17, 20, 17);
                    canvas.FillEllipse(7, 4, 6, 6);
                    canvas.FillEllipse(12, 14, 6, 6);
                    break;
                case AppIconKind.Previous:
                    canvas.DrawLine(15, 5, 8, 12);
                    canvas.DrawLine(8, 12, 15, 19);
                    break;
                case AppIconKind.Next:
                    canvas.DrawLine(9, 5, 16, 12);
                    canvas.DrawLine(16, 12, 9, 19);
                    break;
                case AppIconKind.Target:
                    canvas.DrawEllipse(3, 3, 18, 18);
                    canvas.DrawEllipse(7, 7, 10, 10);
                    canvas.FillEllipse(10, 10, 4, 4);
                    break;
                case AppIconKind.More:
                    canvas.FillEllipse(3, 10, 4, 4);
                    canvas.FillEllipse(10, 10, 4, 4);
                    canvas.FillEllipse(17, 10, 4, 4);
                    break;
                case AppIconKind.Microphone:
                    canvas.DrawRoundedRectangle(9, 2, 6, 12, 3);
                    using (PathF cradle = new())
                    {
                        cradle.MoveTo(6, 10);
                        cradle.LineTo(6, 12);
                        cradle.CurveTo(6, 15.3f, 8.7f, 18, 12, 18);
                        cradle.CurveTo(15.3f, 18, 18, 15.3f, 18, 12);
                        cradle.LineTo(18, 10);
                        canvas.DrawPath(cradle);
                    }
                    canvas.DrawLine(12, 18, 12, 22);
                    canvas.DrawLine(8, 22, 16, 22);
                    break;
            }

            canvas.RestoreState();
        }
    }
}
