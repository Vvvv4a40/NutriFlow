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
    Microphone,
    Plus,
    Search,
    Camera,
    Barcode,
    Edit,
    Expand,
    Warning,
    Check,
    Refresh,
    Key,
    Archive,
    User,
    Sparkle
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
                case AppIconKind.Expand:
                    canvas.DrawLine(5, 9, 12, 16);
                    canvas.DrawLine(12, 16, 19, 9);
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
                case AppIconKind.Plus:
                    canvas.StrokeSize = 2.2f;
                    canvas.DrawLine(12, 5, 12, 19);
                    canvas.DrawLine(5, 12, 19, 12);
                    break;
                case AppIconKind.Search:
                    canvas.DrawEllipse(4, 4, 13, 13);
                    canvas.DrawLine(15, 15, 20, 20);
                    break;
                case AppIconKind.Camera:
                    canvas.DrawRoundedRectangle(3, 7, 18, 13, 3);
                    canvas.DrawLine(8, 7, 9.5f, 4);
                    canvas.DrawLine(9.5f, 4, 14.5f, 4);
                    canvas.DrawLine(14.5f, 4, 16, 7);
                    canvas.DrawEllipse(8.5f, 9.5f, 7, 7);
                    break;
                case AppIconKind.Barcode:
                    canvas.DrawLine(4, 5, 4, 19);
                    canvas.DrawLine(7, 5, 7, 19);
                    canvas.DrawLine(11, 5, 11, 19);
                    canvas.DrawLine(14, 5, 14, 19);
                    canvas.DrawLine(17, 5, 17, 19);
                    canvas.DrawLine(20, 5, 20, 19);
                    break;
                case AppIconKind.Edit:
                    canvas.DrawLine(5, 19, 6, 15);
                    canvas.DrawLine(6, 15, 16, 5);
                    canvas.DrawLine(16, 5, 19, 8);
                    canvas.DrawLine(19, 8, 9, 18);
                    canvas.DrawLine(9, 18, 5, 19);
                    canvas.DrawLine(14, 7, 17, 10);
                    break;
                case AppIconKind.Warning:
                    canvas.DrawLine(12, 3, 21, 20);
                    canvas.DrawLine(21, 20, 3, 20);
                    canvas.DrawLine(3, 20, 12, 3);
                    canvas.DrawLine(12, 9, 12, 14);
                    canvas.FillEllipse(11, 16, 2, 2);
                    break;
                case AppIconKind.Check:
                    canvas.StrokeSize = 2.2f;
                    canvas.DrawLine(5, 12.5f, 10, 17);
                    canvas.DrawLine(10, 17, 19, 7);
                    break;
                case AppIconKind.Refresh:
                    canvas.DrawArc(4, 4, 16, 16, 30, 300, false, false);
                    canvas.DrawLine(19, 3, 19, 8);
                    canvas.DrawLine(19, 8, 14, 8);
                    break;
                case AppIconKind.Key:
                    canvas.DrawEllipse(3, 9, 8, 8);
                    canvas.DrawLine(11, 13, 21, 13);
                    canvas.DrawLine(17, 13, 17, 17);
                    canvas.DrawLine(20, 13, 20, 16);
                    break;
                case AppIconKind.Archive:
                    canvas.DrawRoundedRectangle(3, 4, 18, 5, 1.5f);
                    canvas.DrawRoundedRectangle(5, 9, 14, 11, 2);
                    canvas.DrawLine(10, 13, 14, 13);
                    break;
                case AppIconKind.User:
                    canvas.DrawEllipse(8, 3, 8, 8);
                    using (PathF shoulders = new())
                    {
                        shoulders.MoveTo(4, 21);
                        shoulders.CurveTo(4, 16.5f, 7.6f, 14, 12, 14);
                        shoulders.CurveTo(16.4f, 14, 20, 16.5f, 20, 21);
                        canvas.DrawPath(shoulders);
                    }
                    break;
                case AppIconKind.Sparkle:
                    using (PathF star = new())
                    {
                        star.MoveTo(11, 3);
                        star.CurveTo(11.6f, 7.8f, 13.2f, 9.4f, 18, 10);
                        star.CurveTo(13.2f, 10.6f, 11.6f, 12.2f, 11, 17);
                        star.CurveTo(10.4f, 12.2f, 8.8f, 10.6f, 4, 10);
                        star.CurveTo(8.8f, 9.4f, 10.4f, 7.8f, 11, 3);
                        star.Close();
                        canvas.FillPath(star);
                    }
                    canvas.FillEllipse(17, 15, 4, 4);
                    break;
            }

            canvas.RestoreState();
        }
    }
}
