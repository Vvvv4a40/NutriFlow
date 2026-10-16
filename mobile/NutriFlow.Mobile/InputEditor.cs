namespace NutriFlow.Mobile;

internal sealed class InputEditor : Editor
{
    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName is nameof(Background) or nameof(BackgroundColor))
        {
            FieldBorder.Apply(this);
        }
    }

#if ANDROID
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        FieldBorder.Apply(this);
        if (Handler?.PlatformView is Android.Widget.EditText nativeEditor)
        {
            nativeEditor.Touch -= OnNativeTouch;
            nativeEditor.Touch += OnNativeTouch;
        }
    }

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        if (args.OldHandler?.PlatformView is Android.Widget.EditText nativeEditor)
        {
            nativeEditor.Touch -= OnNativeTouch;
            nativeEditor.Parent?.RequestDisallowInterceptTouchEvent(false);
        }
        base.OnHandlerChanging(args);
    }

    private static void OnNativeTouch(object? sender, Android.Views.View.TouchEventArgs args)
    {
        if (sender is not Android.Widget.EditText nativeEditor)
        {
            return;
        }

        if (args.Event?.ActionMasked == Android.Views.MotionEventActions.Down)
        {
            bool canScroll = nativeEditor.CanScrollVertically(-1) || nativeEditor.CanScrollVertically(1);
            nativeEditor.Parent?.RequestDisallowInterceptTouchEvent(canScroll);
        }
        else if (args.Event?.ActionMasked is Android.Views.MotionEventActions.Up or Android.Views.MotionEventActions.Cancel)
        {
            nativeEditor.Parent?.RequestDisallowInterceptTouchEvent(false);
        }
        args.Handled = false;
    }
#endif
}
