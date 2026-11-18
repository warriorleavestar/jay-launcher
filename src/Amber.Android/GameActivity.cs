using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Amber.Mobile;

namespace Amber.Android;

[Activity(
    Label = "Jay Game",
    Process = ":game",
    Theme = "@style/MyTheme.NoActionBar",
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class GameActivity : Activity, ISurfaceHolderCallback
{
    public const string ServerAddressExtra = "nz.work.minecraft.jay.SERVER_ADDRESS";
    public const string VersionIdExtra = "nz.work.minecraft.jay.VERSION_ID";
    public const string PlayerNameExtra = "nz.work.minecraft.jay.PLAYER_NAME";
    private CancellationTokenSource? _gameSetup;
    private TouchControlOverlay? _touchOverlay;
    private readonly TaskCompletionSource _surfaceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);

        var root = new FrameLayout(this);
        var surface = new SurfaceView(this);
        surface.Holder!.AddCallback(this);
        root.AddView(surface, MatchParent());
        _touchOverlay = new TouchControlOverlay(this)
        {
            Visibility = ViewStates.Gone,
            Alpha = 0
        };
        root.AddView(_touchOverlay, MatchParent());

        SetContentView(root);
        EnterImmersiveMode();
        _gameSetup = new CancellationTokenSource();
        _ = RunPreparedGameAsync(_gameSetup.Token);
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) EnterImmersiveMode();
        _touchOverlay?.UpdateHardwareCapture(hasFocus);
    }

    public override bool DispatchKeyEvent(KeyEvent? e) =>
        _touchOverlay?.HandleHardwareKey(e) == true || base.DispatchKeyEvent(e);

    public override bool DispatchGenericMotionEvent(MotionEvent? e) =>
        _touchOverlay?.HandleHardwareMotion(e) == true || base.DispatchGenericMotionEvent(e);

    public override bool DispatchTouchEvent(MotionEvent? e) =>
        _touchOverlay?.HandleHardwareMotion(e) == true || base.DispatchTouchEvent(e);

    private void EnterImmersiveMode()
    {
        if (Window is not { } window) return;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
#pragma warning disable CA1416, CA1422
            window.SetDecorFitsSystemWindows(false);
            if (window.InsetsController is { } controller)
            {
                controller.Hide(WindowInsets.Type.SystemBars());
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
#pragma warning restore CA1416, CA1422
            return;
        }

#pragma warning disable CS0618
        window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
            SystemUiFlags.Fullscreen |
            SystemUiFlags.HideNavigation |
            SystemUiFlags.ImmersiveSticky |
            SystemUiFlags.LayoutFullscreen |
            SystemUiFlags.LayoutHideNavigation |
            SystemUiFlags.LayoutStable);
#pragma warning restore CS0618
    }

    protected override void OnDestroy()
    {
        _gameSetup?.Cancel();
        _gameSetup?.Dispose();
        _gameSetup = null;
        base.OnDestroy();
    }

    protected override void OnPause()
    {
        _touchOverlay?.ReleaseHardwareInput();
        base.OnPause();
    }

    private async Task RunPreparedGameAsync(CancellationToken ct)
    {
        try
        {
            var versionId = Intent?.GetStringExtra(VersionIdExtra);
            if (string.IsNullOrWhiteSpace(versionId))
                throw new InvalidOperationException("No Minecraft instance was selected.");
            var playerName = Intent?.GetStringExtra(PlayerNameExtra);
            if (string.IsNullOrWhiteSpace(playerName))
                throw new InvalidOperationException("No Minecraft account was selected.");

            // The launcher has already completed all network work. This second pass only rebuilds the invocation
            // inside the isolated game process, then switches directly to Minecraft without a setup screen.
            var prepared = await AndroidEmbeddedGame.PrepareAsync(this, versionId, playerName,
                Intent?.GetStringExtra(ServerAddressExtra), progress: null, ct);
            await _surfaceReady.Task.WaitAsync(ct);
            var runTask = Task.Run(() => AndroidEmbeddedGame.Run(prepared), ct);
            await Task.Delay(900, ct);
            if (_touchOverlay is not null)
            {
                _touchOverlay.Visibility = ViewStates.Visible;
                _touchOverlay.Alpha = 1;
                _touchOverlay.UpdateHardwareCapture(HasWindowFocus);
            }
            await runTask;
            Finish();
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException
                                           or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            global::Android.Util.Log.Error("JayGame", exception.ToString());
            Toast.MakeText(this, exception.Message, ToastLength.Long)?.Show();
            Finish();
        }
    }


    public void SurfaceCreated(ISurfaceHolder holder)
    {
        NativeJvmHost.SetSurface(holder.Surface);
        _surfaceReady.TrySetResult();
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) =>
        NativeJvmHost.SetSurface(holder.Surface);

    public void SurfaceDestroyed(ISurfaceHolder holder) => NativeJvmHost.SetSurface(null);

    private static FrameLayout.LayoutParams MatchParent() => new(
        ViewGroup.LayoutParams.MatchParent,
        ViewGroup.LayoutParams.MatchParent);
}

/// <summary>Touch and hardware input overlay shown only after Minecraft owns the game surface.</summary>
internal sealed class TouchControlOverlay : View, ITouchInputSink
{
    private const long LongPressDelayMs = 340;
    private const long LongPressRingMs = 1150;
    private readonly Paint _fill = new() { AntiAlias = true, Color = Color.Argb(142, 48, 53, 59) };
    private readonly Paint _activeFill = new() { AntiAlias = true, Color = Color.Argb(218, 105, 112, 120) };
    private readonly Paint _buttonFace = new() { AntiAlias = false, Color = Color.Argb(168, 55, 59, 63) };
    private readonly Paint _buttonPressed = new() { AntiAlias = false, Color = Color.Argb(220, 104, 109, 114) };
    private readonly Paint _buttonShadow = new() { AntiAlias = false, Color = Color.Argb(205, 5, 7, 9) };
    private readonly Paint _buttonHighlight = new() { AntiAlias = false, Color = Color.Argb(185, 226, 229, 232), StrokeWidth = 2 };
    private readonly Paint _buttonBorder = new() { AntiAlias = false, Color = Color.Argb(210, 22, 25, 28), StrokeWidth = 2 };
    private readonly Paint _joystickMark = new() { AntiAlias = true, Color = Color.Argb(145, 235, 238, 242), StrokeWidth = 3, StrokeCap = Paint.Cap.Round };
    private readonly Paint _stroke = new() { AntiAlias = true, Color = Color.Argb(210, 235, 238, 242), StrokeWidth = 2, StrokeCap = Paint.Cap.Square, StrokeJoin = Paint.Join.Miter };
    private readonly Paint _glyph = new() { AntiAlias = true, Color = Color.Argb(235, 255, 255, 255), StrokeWidth = 5, StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
    private readonly Paint _hint = new() { AntiAlias = true, Color = Color.Argb(230, 255, 255, 255), TextAlign = Paint.Align.Center };
    private readonly Paint _progressTrack = new() { AntiAlias = true, Color = Color.Argb(110, 16, 20, 24), StrokeWidth = 8, StrokeCap = Paint.Cap.Round };
    private readonly Paint _progress = new() { AntiAlias = true, Color = Color.Argb(235, 255, 255, 255), StrokeWidth = 8, StrokeCap = Paint.Cap.Round };
    private readonly Paint _scrim = new() { AntiAlias = false, Color = Color.Argb(175, 0, 0, 0) };
    private readonly Paint _panel = new() { AntiAlias = false, Color = Color.Argb(245, 24, 27, 31) };
    private readonly TouchInputRouter _router;
    private readonly TouchControlProfile _profile = new();
    private readonly ISharedPreferences _preferences;
    private readonly Dictionary<int, LongPressIndicator> _longPressIndicators = [];
    private readonly HashSet<int> _hotbarPointers = [];
    private readonly HashSet<Keycode> _hardwareKeys = [];
    private readonly HashSet<TouchControlAction> _hardwareActions = [];
    private bool _settingsOpen;
    private bool _layoutEditing;
    private int? _editingPointer;
    private TouchControlAction? _editingAction;
    private float _controlOpacity = 0.74f;
    private float _lastMouseX;
    private float _lastMouseY;
    private bool _hasMousePosition;
    private bool? _lastGameplayControlsActive;

    private readonly SdlTouchInput? _sdl;
    private bool GameplayActive => _sdl?.InWorld ?? NativeJvmHost.GameplayControlsActive;

    public TouchControlOverlay(Context context, SdlTouchInput? sdl = null) : base(context)
    {
        _sdl = sdl;
        _preferences = context.GetSharedPreferences("jay-touch-controls", FileCreationMode.Private)!;
        LoadProfile();
        _router = new TouchInputRouter(this, _profile,
            inventoryStackCount: NativeJvmHost.InventoryStackCount);
        Clickable = true;
        Focusable = true;
        FocusableInTouchMode = true;
    }

    public override bool OnCheckIsTextEditor() => true;

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        if (outAttrs is null) return null;
        outAttrs.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
        outAttrs.ImeOptions = ImeFlags.NoExtractUi;
        return new JayInputConnection(this);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        // This view is composited over a SurfaceView. Clear its transparent layer on every frame;
        // otherwise some Android GPU drivers retain the previous joystick knob position.
        canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear);
        var gameplayControlsActive = GameplayActive;
        UpdateViewportGeometry();
        if (_lastGameplayControlsActive != gameplayControlsActive)
        {
            if (_lastGameplayControlsActive == true && !gameplayControlsActive)
                ReleaseHardwareInput();
            _lastGameplayControlsActive = gameplayControlsActive;
            UpdateHardwareCapture(HasWindowFocus);
        }
        _stroke.SetStyle(Paint.Style.Stroke);
        ApplyControlOpacity();
        foreach (var control in _profile.Controls)
        {
            if (gameplayControlsActive ? !control.GameplayVisible : !control.MenuVisible) continue;
            var x = (float)(control.CenterX * Width);
            var y = (float)(control.CenterY * Height);
            var radius = (float)(control.Radius * Math.Min(Width, Height));
            if (control.Action == TouchControlAction.Move)
                DrawMovementPad(canvas, x, y, radius);
            else
                DrawActionButton(canvas, control, x, y, radius,
                    !gameplayControlsActive && control.Action == TouchControlAction.SecondaryAction
                        ? "拆分"
                        : control.Label);
        }
        DrawLongPressIndicators(canvas);
        DrawInventorySplitSelection(canvas);
        if (_profile.Mode == TouchControlMode.JoystickAndCrosshair && gameplayControlsActive)
            DrawCrosshair(canvas);
        if (_settingsOpen)
            DrawSettings(canvas);
        else if (_layoutEditing)
            DrawEditGuide(canvas);
        PostInvalidateDelayed(100);
    }

    private void DrawMovementPad(Canvas canvas, float x, float y, float radius)
    {
        if (_profile.Mode == TouchControlMode.DPadAndTap)
        {
            DrawDirectionPad(canvas, x, y, radius);
            return;
        }
        var fill = _router.IsPressed(TouchControlAction.Move) ? _activeFill : _fill;
        canvas.DrawCircle(x + radius * 0.045f, y + radius * 0.075f, radius * 1.03f, _buttonShadow);
        canvas.DrawCircle(x, y, radius, fill);
        _stroke.StrokeWidth = Math.Max(2, radius * 0.025f);
        canvas.DrawCircle(x, y, radius, _stroke);
        canvas.DrawCircle(x, y, radius * 0.72f, _stroke);
        _joystickMark.StrokeWidth = Math.Max(2, radius * 0.025f);
        var mark = radius * 0.14f;
        canvas.DrawLine(x, y - radius * 0.86f, x, y - radius * 0.86f + mark, _joystickMark);
        canvas.DrawLine(x, y + radius * 0.86f, x, y + radius * 0.86f - mark, _joystickMark);
        canvas.DrawLine(x - radius * 0.86f, y, x - radius * 0.86f + mark, y, _joystickMark);
        canvas.DrawLine(x + radius * 0.86f, y, x + radius * 0.86f - mark, y, _joystickMark);
        var (moveX, moveY) = _router.Movement;
        var knobX = x + (float)moveX * radius * 0.56f;
        var knobY = y + (float)moveY * radius * 0.56f;
        canvas.DrawCircle(knobX + radius * 0.025f, knobY + radius * 0.045f, radius * 0.36f, _buttonShadow);
        canvas.DrawCircle(knobX, knobY, radius * 0.34f,
            _router.IsPressed(TouchControlAction.Move) ? _buttonPressed : _activeFill);
        canvas.DrawCircle(knobX, knobY, radius * 0.34f, _stroke);
        canvas.DrawCircle(knobX, knobY, radius * 0.075f, _joystickMark);
    }

    private void DrawDirectionPad(Canvas canvas, float x, float y, float radius)
    {
        var cell = radius * 0.64f;
        var (moveX, moveY) = _router.Movement;
        for (var row = -1; row <= 1; row++)
        for (var col = -1; col <= 1; col++)
        {
            if ((row != 0 && col != 0) || (row == 0 && col == 0)) continue;
            var bounds = new RectF(x + col * cell - cell * 0.48f, y + row * cell - cell * 0.48f,
                x + col * cell + cell * 0.48f, y + row * cell + cell * 0.48f);
            var active = _router.IsPressed(TouchControlAction.Move) && col * moveX + row * moveY > 0.45;
            // Direction cells sit next to one another, so an offset drop shadow reads as a
            // second copy of the pad on translucent HUDs. Keep these deliberately single-layer.
            DrawBlockButton(canvas, bounds, active, cell * 0.08f, castShadow: false);
            DrawDirectionGlyph(canvas, x + col * cell, y + row * cell, col, row, cell * 0.28f);
        }
    }

    private void DrawDirectionGlyph(Canvas canvas, float x, float y, int dx, int dy, float size)
    {
        _glyph.SetStyle(Paint.Style.Fill);
        var path = new global::Android.Graphics.Path();
        if (dx != 0)
        {
            path.MoveTo(x + dx * size * 0.72f, y);
            path.LineTo(x - dx * size * 0.44f, y - size * 0.62f);
            path.LineTo(x - dx * size * 0.44f, y + size * 0.62f);
        }
        else
        {
            path.MoveTo(x, y + dy * size * 0.72f);
            path.LineTo(x - size * 0.62f, y - dy * size * 0.44f);
            path.LineTo(x + size * 0.62f, y - dy * size * 0.44f);
        }
        path.Close();
        canvas.DrawPath(path, _glyph);
        _glyph.SetStyle(Paint.Style.Stroke);
    }

    private void DrawCrosshair(Canvas canvas)
    {
        var size = Math.Min(Width, Height) * 0.018f;
        _glyph.StrokeWidth = Math.Max(2, size * 0.15f);
        canvas.DrawLine(Width / 2f - size, Height / 2f, Width / 2f + size, Height / 2f, _glyph);
        canvas.DrawLine(Width / 2f, Height / 2f - size, Width / 2f, Height / 2f + size, _glyph);
    }

    private void DrawEditGuide(Canvas canvas)
    {
        _hint.TextSize = Math.Max(18, Math.Min(Width, Height) * 0.021f);
        canvas.DrawRect(Width * 0.35f, Height * 0.115f, Width * 0.65f, Height * 0.185f, _fill);
        canvas.DrawText("拖动按键调整位置", Width * 0.5f, Height * 0.160f, _hint);
        canvas.DrawRect(Width * 0.87f, Height * 0.02f, Width * 0.98f, Height * 0.10f, _activeFill);
        canvas.DrawText("完成", Width * 0.925f, Height * 0.075f, _hint);
    }

    private void DrawSettings(Canvas canvas)
    {
        var left = Width * 0.60f;
        var right = Width * 0.97f;
        canvas.DrawRect(0, 0, Width, Height, _scrim);
        canvas.DrawRect(left, Height * 0.08f, right, Height * 0.96f, _panel);
        _hint.TextSize = Math.Max(17, Math.Min(Width, Height) * 0.021f);
        canvas.DrawText("触控设置", (left + right) / 2, Height * 0.14f, _hint);
        DrawSettingRow(canvas, "摇杆 + 点击互动", 0.18f, _profile.Mode == TouchControlMode.JoystickAndTap);
        DrawSettingRow(canvas, "方向键 + 点击互动", 0.25f, _profile.Mode == TouchControlMode.DPadAndTap);
        DrawSettingRow(canvas, "摇杆 + 准星", 0.32f, _profile.Mode == TouchControlMode.JoystickAndCrosshair);
        DrawSettingRow(canvas, "外接键盘鼠标", 0.39f, _profile.Mode == TouchControlMode.ExternalKeyboardMouse);
        DrawSettingRow(canvas, $"攻击区域：{AttackAreaLabel(_profile.AttackArea)}", 0.47f, true);
        DrawStepper(canvas, $"视角灵敏度  {Math.Round(_profile.LookSensitivity * 100):0}%", 0.55f);
        DrawStepper(canvas, $"按键大小  {Math.Round(_profile.Scale * 100):0}%", 0.63f);
        DrawStepper(canvas, $"透明度  {Math.Round(_controlOpacity * 100):0}%", 0.71f);
        DrawSettingRow(canvas, _profile.ForcedSprint ? "强制疾跑：开" : "强制疾跑：关", 0.79f,
            _profile.ForcedSprint);
        DrawPanelButton(canvas, "恢复默认", 0.615f, 0.84f, 0.77f, 0.90f);
        DrawPanelButton(canvas, "编辑布局", 0.80f, 0.84f, 0.955f, 0.90f);
        DrawPanelButton(canvas, "完成", 0.70f, 0.91f, 0.87f, 0.955f);
    }

    private void DrawSettingRow(Canvas canvas, string text, float y, bool selected)
    {
        var bounds = new RectF(Width * 0.615f, Height * (y - 0.038f), Width * 0.955f, Height * (y + 0.038f));
        canvas.DrawRect(bounds, selected ? _activeFill : _buttonFace);
        canvas.DrawRect(bounds, _buttonBorder);
        canvas.DrawText(text, Width * 0.785f, Height * (y + 0.012f), _hint);
    }

    private void DrawStepper(Canvas canvas, string text, float y)
    {
        DrawPanelButton(canvas, "−", 0.615f, y - 0.038f, 0.675f, y + 0.038f);
        DrawPanelButton(canvas, "+", 0.895f, y - 0.038f, 0.955f, y + 0.038f);
        canvas.DrawText(text, Width * 0.785f, Height * (y + 0.012f), _hint);
    }

    private void DrawPanelButton(Canvas canvas, string text, float left, float top, float right, float bottom)
    {
        var bounds = new RectF(Width * left, Height * top, Width * right, Height * bottom);
        canvas.DrawRect(bounds, _buttonFace);
        canvas.DrawRect(bounds, _buttonBorder);
        canvas.DrawText(text, Width * ((left + right) / 2), Height * ((top + bottom) / 2 + 0.012f), _hint);
    }

    private void ApplyControlOpacity()
    {
        static Color WithAlpha(Color color, float opacity) => Color.Argb(
            (int)Math.Clamp(color.A * opacity, 0, 255), color.R, color.G, color.B);
        _fill.Color = WithAlpha(Color.Argb(142, 48, 53, 59), _controlOpacity);
        _activeFill.Color = WithAlpha(Color.Argb(218, 105, 112, 120), _controlOpacity);
        _buttonFace.Color = WithAlpha(Color.Argb(168, 55, 59, 63), _controlOpacity);
        _buttonPressed.Color = WithAlpha(Color.Argb(220, 104, 109, 114), _controlOpacity);
        _buttonShadow.Color = WithAlpha(Color.Argb(205, 5, 7, 9), Math.Max(0.62f, _controlOpacity));
        _buttonHighlight.Color = WithAlpha(Color.Argb(185, 226, 229, 232), Math.Max(0.55f, _controlOpacity));
        _buttonBorder.Color = WithAlpha(Color.Argb(210, 22, 25, 28), Math.Max(0.62f, _controlOpacity));
        _joystickMark.Color = WithAlpha(Color.Argb(145, 235, 238, 242), Math.Max(0.45f, _controlOpacity));
    }

    private void DrawLongPressIndicators(Canvas canvas)
    {
        var now = SystemClock.UptimeMillis();
        var scale = Math.Min(Width, Height);
        // The indicator must remain visible around a resting fingertip on a tablet.
        var radius = scale * 0.058f;
        _progressTrack.SetStyle(Paint.Style.Stroke);
        _progress.SetStyle(Paint.Style.Stroke);
        _progressTrack.StrokeWidth = Math.Max(5, scale * 0.0045f);
        _progress.StrokeWidth = _progressTrack.StrokeWidth;

        foreach (var (pointerId, indicator) in _longPressIndicators.ToArray())
        {
            // Match the mobile interaction: an ordinary touch has no decoration. The ring starts
            // only after the gesture has become a mining hold and the crosshair is on a block.
            if (!_router.IsLongPress(pointerId) || !NativeJvmHost.HasBlockTarget) continue;
            var miningElapsed = now - indicator.DownAt - LongPressDelayMs;
            var x = (float)(indicator.X * Width);
            var y = (float)(indicator.Y * Height);
            var bounds = new RectF(x - radius, y - radius, x + radius, y + radius);
            canvas.DrawCircle(x, y, radius, _progressTrack);
            var amount = Math.Clamp(miningElapsed / (double)LongPressRingMs, 0, 1);
            canvas.DrawArc(bounds, -90, (float)(amount * 360), false, _progress);
            canvas.DrawCircle(x, y, radius * 0.16f,
                _router.IsLongPress(pointerId) ? _activeFill : _fill);
        }
    }

    private void DrawInventorySplitSelection(Canvas canvas)
    {
        foreach (var selection in _router.InventorySplitSelections)
        {
            var scale = Math.Min(Width, Height);
            var width = Math.Max(scale * 0.25f, 210f);
            var height = Math.Max(scale * 0.038f, 32f);
            var centerX = Math.Clamp((float)(selection.X * Width), width * 0.55f,
                Width - width * 0.55f);
            var bottom = Math.Max(height * 1.5f,
                (float)(selection.Y * Height) - scale * 0.055f);
            var bounds = new RectF(centerX - width / 2, bottom - height,
                centerX + width / 2, bottom);
            canvas.DrawRect(bounds, _buttonShadow);
            var fill = new RectF(bounds.Left, bounds.Top,
                bounds.Left + bounds.Width() * selection.Count / selection.Total, bounds.Bottom);
            canvas.DrawRect(fill, _activeFill);
            canvas.DrawRect(bounds, _buttonBorder);
            _hint.TextSize = Math.Max(18, height * 0.62f);
            canvas.DrawText($"{selection.Count}/{selection.Total}", centerX,
                bounds.CenterY() - (_hint.Ascent() + _hint.Descent()) / 2, _hint);
        }
    }

    private void DrawActionButton(Canvas canvas, TouchControlDefinition control,
        float x, float y, float radius, string label)
    {
        var compact = control.Action is TouchControlAction.PreviousSlot or TouchControlAction.NextSlot;
        var utility = control.Action is TouchControlAction.Menu or TouchControlAction.Keyboard or
            TouchControlAction.Inventory or TouchControlAction.Drop;
        var halfWidth = radius * (compact ? 0.78f : utility ? 0.82f : 0.92f);
        var halfHeight = radius * (compact ? 0.78f : utility ? 0.82f : 0.92f);
        var pressed = _router.IsPressed(control.Action);
        var pressOffset = pressed ? Math.Max(1, radius * 0.08f) : 0;
        var bounds = new RectF((float)Math.Round(x - halfWidth), (float)Math.Round(y - halfHeight),
            (float)Math.Round(x + halfWidth), (float)Math.Round(y + halfHeight));
        bounds.Offset(0, pressOffset);
        var castShadow = control.Action != TouchControlAction.Inventory &&
            !(_profile.Mode == TouchControlMode.DPadAndTap &&
              control.Action == TouchControlAction.Sneak);
        DrawBlockButton(canvas, bounds, pressed, radius * 0.10f, castShadow);
        DrawGlyph(canvas, control.Action, x, y + pressOffset,
            Math.Min(halfWidth, halfHeight) * 0.68f, label);
    }

    private void DrawBlockButton(Canvas canvas, RectF bounds, bool pressed, float depth,
        bool castShadow = true)
    {
        if (castShadow)
        {
            var shadow = new RectF(bounds);
            shadow.Offset(Math.Max(2, depth * 0.7f), Math.Max(3, depth));
            canvas.DrawRect(shadow, _buttonShadow);
        }
        canvas.DrawRect(bounds, pressed ? _buttonPressed : _buttonFace);
        _buttonBorder.SetStyle(Paint.Style.Stroke);
        _buttonBorder.StrokeWidth = Math.Max(2, depth * 0.24f);
        canvas.DrawRect(bounds, _buttonBorder);
        if (castShadow)
        {
            _buttonHighlight.StrokeWidth = Math.Max(1, depth * 0.20f);
            var inset = Math.Max(2, depth * 0.30f);
            canvas.DrawLine(bounds.Left + inset, bounds.Top + inset,
                bounds.Right - inset, bounds.Top + inset, _buttonHighlight);
            canvas.DrawLine(bounds.Left + inset, bounds.Top + inset,
                bounds.Left + inset, bounds.Bottom - inset, _buttonHighlight);
        }
    }

    private void DrawGlyph(Canvas canvas, TouchControlAction action, float x, float y, float size, string fallback)
    {
        _glyph.SetStyle(Paint.Style.Stroke);
        _glyph.StrokeWidth = Math.Max(3, size * 0.12f);
        var path = new global::Android.Graphics.Path();
        switch (action)
        {
            case TouchControlAction.Menu:
                canvas.DrawLine(x - size * 0.27f, y - size * 0.45f, x - size * 0.27f, y + size * 0.45f, _glyph);
                canvas.DrawLine(x + size * 0.27f, y - size * 0.45f, x + size * 0.27f, y + size * 0.45f, _glyph);
                break;
            case TouchControlAction.Close:
                canvas.DrawLine(x - size * 0.46f, y - size * 0.46f,
                    x + size * 0.46f, y + size * 0.46f, _glyph);
                canvas.DrawLine(x + size * 0.46f, y - size * 0.46f,
                    x - size * 0.46f, y + size * 0.46f, _glyph);
                break;
            case TouchControlAction.Keyboard:
                canvas.DrawRect(x - size * 0.62f, y - size * 0.42f,
                    x + size * 0.62f, y + size * 0.28f, _glyph);
                path.MoveTo(x - size * 0.34f, y + size * 0.28f);
                path.LineTo(x - size * 0.52f, y + size * 0.58f);
                path.LineTo(x - size * 0.02f, y + size * 0.28f);
                canvas.DrawPath(path, _glyph);
                canvas.DrawLine(x + size * 0.24f, y - size * 0.26f,
                    x - size * 0.10f, y + size * 0.12f, _glyph);
                break;
            case TouchControlAction.Settings:
                canvas.DrawCircle(x, y, size * 0.36f, _glyph);
                for (var i = 0; i < 8; i++)
                {
                    var angle = i * Math.PI / 4;
                    canvas.DrawLine(x + (float)Math.Cos(angle) * size * 0.48f,
                        y + (float)Math.Sin(angle) * size * 0.48f,
                        x + (float)Math.Cos(angle) * size * 0.68f,
                        y + (float)Math.Sin(angle) * size * 0.68f, _glyph);
                }
                break;
            case TouchControlAction.Inventory:
                _glyph.SetStyle(Paint.Style.Fill);
                for (var col = -1; col <= 1; col++)
                    canvas.DrawRect(x + col * size * 0.48f - size * 0.12f, y - size * 0.12f,
                        x + col * size * 0.48f + size * 0.12f, y + size * 0.12f, _glyph);
                _glyph.SetStyle(Paint.Style.Stroke);
                break;
            case TouchControlAction.Drop:
                canvas.DrawLine(x, y - size * 0.62f, x, y + size * 0.28f, _glyph);
                path.MoveTo(x - size * 0.30f, y); path.LineTo(x, y + size * 0.36f); path.LineTo(x + size * 0.30f, y); canvas.DrawPath(path, _glyph);
                canvas.DrawLine(x - size * 0.40f, y + size * 0.58f, x + size * 0.40f, y + size * 0.58f, _glyph);
                break;
            case TouchControlAction.Jump:
                path.MoveTo(x - size * 0.56f, y + size * 0.18f);
                path.LineTo(x, y - size * 0.48f);
                path.LineTo(x + size * 0.56f, y + size * 0.18f);
                canvas.DrawPath(path, _glyph);
                canvas.DrawLine(x, y - size * 0.38f, x, y + size * 0.52f, _glyph);
                break;
            case TouchControlAction.Sneak:
                path.MoveTo(x - size * 0.54f, y - size * 0.18f);
                path.LineTo(x, y + size * 0.44f);
                path.LineTo(x + size * 0.54f, y - size * 0.18f);
                canvas.DrawPath(path, _glyph);
                canvas.DrawLine(x, y - size * 0.50f, x, y + size * 0.34f, _glyph);
                canvas.DrawLine(x - size * 0.44f, y + size * 0.56f,
                    x + size * 0.44f, y + size * 0.56f, _glyph);
                break;
            case TouchControlAction.Sprint:
                DrawChevron(canvas, x - size * 0.22f, y, size * 0.56f);
                DrawChevron(canvas, x + size * 0.22f, y, size * 0.56f);
                break;
            case TouchControlAction.PrimaryAction:
                canvas.Save();
                canvas.Rotate(-42, x, y);
                canvas.DrawLine(x, y - size * 0.70f, x, y + size * 0.34f, _glyph);
                canvas.DrawLine(x - size * 0.34f, y + size * 0.10f, x + size * 0.34f, y + size * 0.10f, _glyph);
                canvas.DrawLine(x - size * 0.18f, y + size * 0.54f, x + size * 0.18f, y + size * 0.54f, _glyph);
                canvas.Restore();
                break;
            case TouchControlAction.SecondaryAction:
                if (fallback == "拆分")
                {
                    canvas.DrawRect(x - size * 0.62f, y - size * 0.44f,
                        x - size * 0.08f, y + size * 0.10f, _glyph);
                    canvas.DrawRect(x + size * 0.08f, y - size * 0.10f,
                        x + size * 0.62f, y + size * 0.44f, _glyph);
                    canvas.DrawLine(x - size * 0.28f, y + size * 0.36f,
                        x + size * 0.28f, y - size * 0.36f, _glyph);
                    break;
                }
                path.MoveTo(x, y - size * 0.62f);
                path.LineTo(x + size * 0.52f, y - size * 0.30f);
                path.LineTo(x, y + size * 0.02f);
                path.LineTo(x - size * 0.52f, y - size * 0.30f);
                path.Close();
                canvas.DrawPath(path, _glyph);
                canvas.DrawLine(x - size * 0.52f, y - size * 0.30f, x - size * 0.52f, y + size * 0.30f, _glyph);
                canvas.DrawLine(x + size * 0.52f, y - size * 0.30f, x + size * 0.52f, y + size * 0.30f, _glyph);
                canvas.DrawLine(x - size * 0.52f, y + size * 0.30f, x, y + size * 0.62f, _glyph);
                canvas.DrawLine(x + size * 0.52f, y + size * 0.30f, x, y + size * 0.62f, _glyph);
                canvas.DrawLine(x, y + size * 0.02f, x, y + size * 0.62f, _glyph);
                break;
            case TouchControlAction.PreviousSlot:
                DrawChevron(canvas, x + size * 0.10f, y, -size * 0.82f);
                break;
            case TouchControlAction.NextSlot:
                DrawChevron(canvas, x - size * 0.10f, y, size * 0.82f);
                break;
            default:
                _hint.TextSize = Math.Max(14, size * 0.55f);
                canvas.DrawText(fallback, x, y - (_hint.Ascent() + _hint.Descent()) / 2, _hint);
                break;
        }
    }

    private bool TryGetHotbarSlot(double x, double y, out int slot) =>
        TouchHudGeometry.ForAspectRatio(CurrentAspectRatio()).TryGetSlot(x, y, out slot);

    private double CurrentAspectRatio() => Height > 0 ? Width / (double)Height : 16d / 9d;

    private void UpdateViewportGeometry() => _profile.ViewportAspectRatio = CurrentAspectRatio();

        var path = new global::Android.Graphics.Path();
        var direction = Math.Sign(size);
        var reach = Math.Abs(size) * 0.50f;
        path.MoveTo(x - direction * reach, y - reach);
        path.LineTo(x + direction * reach, y);
        path.LineTo(x - direction * reach, y + reach);
        canvas.DrawPath(path, _glyph);
    }

    protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
    {
        base.OnSizeChanged(width, height, oldWidth, oldHeight);
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q) return;
#pragma warning disable CA1416
        SystemGestureExclusionRects = [new Rect(0, Math.Max(0, height - 200), width, height)];
#pragma warning restore CA1416
    }

    private void LoadProfile()
    {
        var layoutVersion = _preferences.GetInt("layout-version", 1);
        _profile.LookSensitivity = Math.Clamp(_preferences.GetFloat("look-sensitivity", 1.65f), 0.50, 3.00);
        _profile.AttackArea = Enum.TryParse<TouchAttackArea>(_preferences.GetString("attack-area", null), out var attackArea)
            ? attackArea : TouchAttackArea.Anywhere;
diff3: invalid print range
