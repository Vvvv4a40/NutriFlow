#if ANDROID
using Android.Graphics.Drawables;
using Microsoft.Maui.Platform;
#endif

namespace NutriFlow.Mobile;

internal static class FieldBorder
{
    public static void Apply(VisualElement field)
    {
#if ANDROID
        if (field.Handler?.PlatformView is not Android.Widget.EditText nativeField)
        {
            return;
        }

        float density = nativeField.Context?.Resources?.DisplayMetrics?.Density ?? 1;
        Color fill = field.BackgroundColor ?? Ui.SurfaceRaised;
        StateListDrawable background = new();
        background.AddState([-Android.Resource.Attribute.StateEnabled], Rounded(fill.WithAlpha(0.5f), Ui.FieldStroke.WithAlpha(0.5f), 1, density));
        background.AddState([Android.Resource.Attribute.StateFocused], Rounded(fill, Ui.FieldFocus, 2, density));
        background.AddState([], Rounded(fill, Ui.FieldStroke, 1, density));
        nativeField.BackgroundTintList = null;
        nativeField.Background = background;
        int horizontal = (int)Math.Round(16 * density);
        int vertical = (int)Math.Round(12 * density);
        nativeField.SetPaddingRelative(horizontal, vertical, horizontal, vertical);
#endif
    }

#if ANDROID
    private static GradientDrawable Rounded(Color fill, Color stroke, float strokeWidth, float density)
    {
        GradientDrawable drawable = new();
        drawable.SetCornerRadius(14 * density);
        drawable.SetColor(fill.ToPlatform());
        drawable.SetStroke(Math.Max(1, (int)Math.Round(strokeWidth * density)), stroke.ToPlatform());
        return drawable;
    }
#endif
}
