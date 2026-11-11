using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Amber.Core.Localization;
using Amber.Mobile;

namespace Amber.Android;

[Activity(
    Label = "Jay Game",
    Theme = "@style/MyTheme.NoActionBar",
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class GameActivity : Activity, ISurfaceHolderCallback
{
    public const string ServerAddressExtra = "nz.work.minecraft.jay.SERVER_ADDRESS";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);

        var root = new FrameLayout(this);
        var surface = new SurfaceView(this);
        surface.SetBackgroundColor(Color.Black);
        surface.Holder!.AddCallback(this);
        root.AddView(surface, MatchParent());
        root.AddView(new TouchControlOverlay(this), MatchParent());

        if (!AndroidRuntimeStore.IsInstalled(this))
        {
            var status = new TextView(this)
            {
                Text = L.T("mobile.runtime.missing"),
                Gravity = GravityFlags.Center
            };
            status.SetTextColor(Color.White);
            status.SetBackgroundColor(Color.Argb(210, 20, 24, 30));
            status.SetPadding(32, 24, 32, 24);
            var panel = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent,
                GravityFlags.Center)
            {
                LeftMargin = 48,
                RightMargin = 48
            };
            root.AddView(status, panel);
        }

        SetContentView(root);
    }

    public void SurfaceCreated(ISurfaceHolder holder) => NativeJvmHost.SetSurface(holder.Surface);

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) =>
        NativeJvmHost.SetSurface(holder.Surface);

    public void SurfaceDestroyed(ISurfaceHolder holder) => NativeJvmHost.SetSurface(null);

    private static FrameLayout.LayoutParams MatchParent() => new(
        ViewGroup.LayoutParams.MatchParent,
        ViewGroup.LayoutParams.MatchParent);
}

internal sealed class TouchControlOverlay : View, ITouchInputSink
{
    private readonly Paint _fill = new() { AntiAlias = true, Color = Color.Argb(110, 25, 29, 36) };
    private readonly Paint _stroke = new() { AntiAlias = true, Color = Color.Argb(210, 238, 174, 78), StrokeWidth = 3, StrokeCap = Paint.Cap.Round };
    private readonly Paint _text = new() { AntiAlias = true, Color = Color.White, TextAlign = Paint.Align.Center, TextSize = 24 };
    private readonly TouchInputRouter _router;

    public TouchControlOverlay(Context context) : base(context) => _router = new TouchInputRouter(this);

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        _stroke.SetStyle(Paint.Style.Stroke);
        foreach (var control in TouchControlLayout.Default)
        {
            var x = (float)(control.CenterX * Width);
            var y = (float)(control.CenterY * Height);
            var radius = (float)(control.Radius * Math.Min(Width, Height));
            canvas.DrawCircle(x, y, radius, _fill);
            canvas.DrawCircle(x, y, radius, _stroke);
            if (control.Label.Length > 0)
                canvas.DrawText(control.Label, x, y - (_text.Ascent() + _text.Descent()) / 2, _text);
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        var actionIndex = e.ActionIndex;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                return _router.Press(e.GetPointerId(actionIndex), e.GetX(actionIndex) / Width, e.GetY(actionIndex) / Height);
            case MotionEventActions.Move:
                var handled = false;
                for (var i = 0; i < e.PointerCount; i++)
                    handled |= _router.Move(e.GetPointerId(i), e.GetX(i) / Width, e.GetY(i) / Height);
                return handled;
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                return _router.Release(e.GetPointerId(actionIndex));
            case MotionEventActions.Cancel:
                _router.ReleaseAll();
                return true;
            default:
                return false;
        }
    }

    public void SetAction(TouchControlAction action, bool pressed) =>
        AndroidGameInputBridge.SetAction(action, pressed);

    public void SetMovement(double x, double y) => AndroidGameInputBridge.SetMovement(x, y);
}
