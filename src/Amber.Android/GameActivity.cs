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
    // Minecraft owns an independent OpenGL SurfaceView. Disabling Android HWUI for the
    // surrounding Activity avoids an Android 15 RenderThread teardown race during the first
    // embedded-JVM launch; it does not disable or slow the game's OpenGL renderer.
    HardwareAccelerated = false,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class GameActivity : Activity, ISurfaceHolderCallback
{
    public const string ServerAddressExtra = "nz.work.minecraft.jay.SERVER_ADDRESS";
    public const string VersionIdExtra = "nz.work.minecraft.jay.VERSION_ID";
    public const string PlayerNameExtra = "nz.work.minecraft.jay.PLAYER_NAME";
    private CancellationTokenSource? _gameSetup;
    private TouchControlOverlay? _touchOverlay;
    private readonly object _surfaceSync = new();
    private TaskCompletionSource _surfaceReady = NewSurfaceReadySource();
    private bool _surfaceAvailable;

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

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        // Android's default Back handling finishes this Activity. In the dedicated game process
        // that immediately reaches OnDestroy and kills HotSpot, including an integrated server
        // that may still be saving. Treat Back as Minecraft Escape once the game owns the view:
        // the pause/menu flow can then save and quit normally before main() returns.
        if (e?.KeyCode == Keycode.Back && _touchOverlay?.Visibility == ViewStates.Visible)
        {
            if (e.Action == KeyEventActions.Down && e.RepeatCount == 0)
                _touchOverlay.HandleSystemBack();
            return true;
        }

        return _touchOverlay?.HandleHardwareKey(e) == true || base.DispatchKeyEvent(e);
    }

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
        _touchOverlay?.CancelAllInput();
        _gameSetup?.Cancel();
        _gameSetup?.Dispose();
        _gameSetup = null;
        base.OnDestroy();
        // This Activity owns an embedded HotSpot VM in the dedicated :game process. HotSpot does
        // not support creating a second VM after main() returns or startup fails. Leaving this
        // process alive makes the cold-start retry (and every later launch) fail with JNI_EEXIST.
        // The launcher runs in the package's main process, so terminating this one is isolated.
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    protected override void OnPause()
    {
        _touchOverlay?.CancelAllInput();
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
            await WaitForSurfaceAsync(ct);
            // A freshly installed/cold Android process is still draining ART and UI finalizers
            // when the surface first appears. Starting HotSpot in that window can replace signal
            // handlers while those threads are active and crash the first launch only. Let the
            // Android side become quiescent before creating the embedded VM.
            await Task.Delay(1800, ct);
            // The Activity may have been backgrounded while it was settling. A completed TCS
            // only proves that a surface existed earlier; never start HotSpot against a surface
            // that Android has already destroyed.
            await WaitForSurfaceAsync(ct);
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
        catch (Exception exception)
        {
            global::Android.Util.Log.Error("JayGame", exception.ToString());
            Toast.MakeText(this, exception.Message, ToastLength.Long)?.Show();
            Finish();
        }
    }


    public void SurfaceCreated(ISurfaceHolder holder)
    {
        NativeJvmHost.SetSurface(holder.Surface);
        lock (_surfaceSync)
        {
            _surfaceAvailable = true;
            _surfaceReady.TrySetResult();
        }
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
    {
        NativeJvmHost.SetSurface(holder.Surface);
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        lock (_surfaceSync)
        {
            _surfaceAvailable = false;
            if (_surfaceReady.Task.IsCompleted)
                _surfaceReady = NewSurfaceReadySource();
        }
        NativeJvmHost.SetSurface(null);
    }

    private async Task WaitForSurfaceAsync(CancellationToken ct)
    {
        while (true)
        {
            Task ready;
            lock (_surfaceSync)
            {
                if (_surfaceAvailable) return;
                ready = _surfaceReady.Task;
            }
            await ready.WaitAsync(ct);
        }
    }

    private static TaskCompletionSource NewSurfaceReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static FrameLayout.LayoutParams MatchParent() => new(
        ViewGroup.LayoutParams.MatchParent,
        ViewGroup.LayoutParams.MatchParent);
}

/// <summary>Touch and hardware input overlay shown only after Minecraft owns the game surface.</summary>
internal sealed class TouchControlOverlay : View, ITouchInputSink
{
    private const int GlfwKeyEscape = 256;
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
    private readonly Dictionary<int, long> _longPressTokens = [];
    private readonly HashSet<int> _hotbarPointers = [];
    private readonly HashSet<Keycode> _hardwareKeys = [];
    private readonly HashSet<TouchControlAction> _hardwareActions = [];
    private readonly HardwareTextInputGate _hardwareTextInputGate = new();
    private bool _settingsOpen;
    private bool _layoutEditing;
    private int? _editingPointer;
    private TouchControlAction? _editingAction;
    private float _controlOpacity = 0.74f;
    private float _lastMouseX;
    private float _lastMouseY;
    private bool _hasMousePosition;
    private bool? _lastGameplayControlsActive;
    private bool? _lastTextInputFocused;
    private long _nextLongPressToken;

    private readonly SdlTouchInput? _sdl;
    private bool _manualKeyboardRequested;
    private bool GameplayActive => _sdl?.InWorld ?? NativeJvmHost.GameplayControlsActive;

    public TouchControlOverlay(Context context, SdlTouchInput? sdl = null) : base(context)
    {
        _sdl = sdl;
        // The game itself renders into the SurfaceView. Keep the transparent controls on a
        // software layer so Android's HWUI worker cannot race the JVM/EGL surface startup and
        // lock a RenderNode mutex after it has been destroyed (seen as a first-launch SIGABRT
        // on Android 15/emulator graphics drivers).
        SetLayerType(LayerType.Software, null);
        _preferences = context.GetSharedPreferences("jay-touch-controls", FileCreationMode.Private)!;
        LoadProfile();
        _router = new TouchInputRouter(this, _profile,
            inventoryStackCount: sdl is null ? NativeJvmHost.InventoryStackCount : sdl.InventoryStackCount);
        Clickable = true;
        Focusable = true;
        FocusableInTouchMode = true;
    }

    // Only advertise an Android text editor while Minecraft actually owns a text field.
    // Keeping this true during gameplay makes physical W/A/S/D key presses wake the IME
    // on tablets, which covers half the screen and prevents keyboard/mouse play.
    private int TextInputFocusState => _sdl is not null
        ? (_sdl.TextInputActive ? 1 : 0)
        : NativeJvmHost.TextInputFocusState;
    private bool TextInputFocused => SoftKeyboardInputPolicy.AcceptsConnection(
        TextInputFocusState, _manualKeyboardRequested);

    public override bool OnCheckIsTextEditor() => TextInputFocused;

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        if (outAttrs is null || !TextInputFocused) return null;
        outAttrs.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
        outAttrs.ImeOptions = ImeFlags.NoExtractUi;
        return new JayInputConnection(this);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        // This view is composited over a SurfaceView. Clear its transparent layer on every frame;
        // otherwise some Android GPU drivers retain the previous joystick knob position.
        canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear!);
        var gameplayControlsActive = GameplayActive;
        var textInputFocused = TextInputFocused;
        if (_lastTextInputFocused != textInputFocused)
        {
            _lastTextInputFocused = textInputFocused;
            // Minecraft auto-focuses some fields while opening a screen (notably the world-list
            // search box). Do not cover the new screen with the IME until the player actually
            // taps the editor; focus loss may still dismiss a keyboard that is already open.
            if (!textInputFocused) Post(HideKeyboard);
        }
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

    private void DrawChevron(Canvas canvas, float x, float y, float size)
    {
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
        _profile.Mode = Enum.TryParse<TouchControlMode>(_preferences.GetString("mode", null), out var mode)
            ? mode : TouchControlMode.DPadAndTap;
        _profile.Scale = Math.Clamp(_preferences.GetFloat("scale", 1f), 0.70, 1.35);
        _profile.LookSensitivity = Math.Clamp(_preferences.GetFloat("look-sensitivity", 1.65f), 0.50, 3.00);
        _profile.AttackArea = Enum.TryParse<TouchAttackArea>(_preferences.GetString("attack-area", null), out var attackArea)
            ? attackArea : TouchAttackArea.Anywhere;
        _profile.ForcedSprint = _preferences.GetBoolean("forced-sprint", true);
        _controlOpacity = Math.Clamp(_preferences.GetFloat("opacity", 0.74f), 0.30f, 1f);
        foreach (var modeValue in Enum.GetValues<TouchControlMode>())
        foreach (var action in Enum.GetValues<TouchControlAction>())
        {
            // v1 stored the inventory key against a fixed 16:9 coordinate. Dropping only that
            // stale override lets the new aspect-aware anchor repair existing tablet installs.
            if (layoutVersion < 2 && action == TouchControlAction.Inventory) continue;
            var x = _preferences.GetFloat($"position-{modeValue}-{action}-x", float.NaN);
            var y = _preferences.GetFloat($"position-{modeValue}-{action}-y", float.NaN);
            if (!float.IsNaN(x) && !float.IsNaN(y)) _profile.SetPosition(modeValue, action, x, y);
        }
        if (layoutVersion < 2)
        {
            using var editor = _preferences.Edit()!;
            foreach (var modeValue in Enum.GetValues<TouchControlMode>())
            {
                editor.Remove($"position-{modeValue}-{TouchControlAction.Inventory}-x");
                editor.Remove($"position-{modeValue}-{TouchControlAction.Inventory}-y");
            }
            editor.PutInt("layout-version", 2);
            editor.Apply();
        }
    }

    private void SaveProfile()
    {
        using var editor = _preferences.Edit()!;
        editor.PutString("mode", _profile.Mode.ToString());
        editor.PutFloat("scale", (float)_profile.Scale);
        editor.PutFloat("look-sensitivity", (float)_profile.LookSensitivity);
        editor.PutString("attack-area", _profile.AttackArea.ToString());
        editor.PutFloat("opacity", _controlOpacity);
        editor.PutBoolean("forced-sprint", _profile.ForcedSprint);
        editor.PutInt("layout-version", 2);
        foreach (var modeValue in Enum.GetValues<TouchControlMode>())
        foreach (var action in Enum.GetValues<TouchControlAction>())
        {
            if (!_profile.TryGetPosition(modeValue, action, out var position)) continue;
            editor.PutFloat($"position-{modeValue}-{action}-x", (float)position.X);
            editor.PutFloat($"position-{modeValue}-{action}-y", (float)position.Y);
        }
        editor.Apply();
    }

    private bool HandleSettingsTouch(MotionEvent e)
    {
        if (!_settingsOpen) return false;
        if (e.ActionMasked is not (MotionEventActions.Down or MotionEventActions.PointerDown)) return true;
        var x = e.GetX(e.ActionIndex) / Width;
        var y = e.GetY(e.ActionIndex) / Height;
        if (x < 0.60f) return true;
        var previousMode = _profile.Mode;
        if (y is > 0.145f and < 0.215f) _profile.Mode = TouchControlMode.JoystickAndTap;
        else if (y is > 0.215f and < 0.285f) _profile.Mode = TouchControlMode.DPadAndTap;
        else if (y is > 0.285f and < 0.355f) _profile.Mode = TouchControlMode.JoystickAndCrosshair;
        else if (y is > 0.355f and < 0.425f) _profile.Mode = TouchControlMode.ExternalKeyboardMouse;
        else if (y is > 0.43f and < 0.51f) _profile.AttackArea = NextAttackArea(_profile.AttackArea);
        else if (y is > 0.51f and < 0.59f && x < 0.70f) _profile.LookSensitivity = Math.Max(0.50, _profile.LookSensitivity - 0.10);
        else if (y is > 0.51f and < 0.59f && x > 0.87f) _profile.LookSensitivity = Math.Min(3.00, _profile.LookSensitivity + 0.10);
        else if (y is > 0.59f and < 0.67f && x < 0.70f) _profile.Scale = Math.Max(0.70, _profile.Scale - 0.05);
        else if (y is > 0.59f and < 0.67f && x > 0.87f) _profile.Scale = Math.Min(1.35, _profile.Scale + 0.05);
        else if (y is > 0.67f and < 0.75f && x < 0.70f) _controlOpacity = Math.Max(0.30f, _controlOpacity - 0.05f);
        else if (y is > 0.67f and < 0.75f && x > 0.87f) _controlOpacity = Math.Min(1f, _controlOpacity + 0.05f);
        else if (y is > 0.75f and < 0.83f) _profile.ForcedSprint = !_profile.ForcedSprint;
        else if (y is > 0.84f and < 0.90f && x < 0.78f)
        {
            _profile.ResetPositions();
            _profile.Scale = 1;
            _profile.LookSensitivity = 1.65;
            _profile.AttackArea = TouchAttackArea.Anywhere;
            _controlOpacity = 0.74f;
            _profile.ForcedSprint = true;
        }
        else if (y is > 0.84f and < 0.90f && x > 0.78f)
        {
            _settingsOpen = false;
            _layoutEditing = true;
        }
        else if (y > 0.90f)
            _settingsOpen = false;
        if (_profile.Mode != previousMode &&
            (previousMode == TouchControlMode.ExternalKeyboardMouse ||
             _profile.Mode == TouchControlMode.ExternalKeyboardMouse))
        {
            ReleaseHardwareInput();
            UpdateHardwareCapture(HasWindowFocus);
        }
        SaveProfile();
        Invalidate();
        return true;
    }

    private static TouchAttackArea NextAttackArea(TouchAttackArea area) => area switch
    {
        TouchAttackArea.Anywhere => TouchAttackArea.RightSide,
        TouchAttackArea.RightSide => TouchAttackArea.Crosshair,
        _ => TouchAttackArea.Anywhere
    };

    private static string AttackAreaLabel(TouchAttackArea area) => area switch
    {
        TouchAttackArea.Anywhere => "全屏任意位置",
        TouchAttackArea.RightSide => "右侧视角区",
        TouchAttackArea.Crosshair => "准星附近",
        _ => "全屏任意位置"
    };

    private bool HandleLayoutEditTouch(MotionEvent e)
    {
        if (!_layoutEditing) return false;
        var index = e.ActionIndex;
        var x = e.GetX(index) / Width;
        var y = e.GetY(index) / Height;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                if (x > 0.87 && y < 0.11)
                {
                    _layoutEditing = false;
                    SaveProfile();
                    Invalidate();
                    return true;
                }
                var control = _profile.HitTest(x, y);
                if (control is null) return true;
                _editingPointer = e.GetPointerId(index);
                _editingAction = control.Action;
                return true;
            case MotionEventActions.Move:
                if (_editingPointer is not { } pointer || _editingAction is not { } action) return true;
                var pointerIndex = e.FindPointerIndex(pointer);
                if (pointerIndex < 0) return true;
                _profile.SetPosition(action, e.GetX(pointerIndex) / Width, e.GetY(pointerIndex) / Height);
                Invalidate();
                return true;
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
            case MotionEventActions.Cancel:
                _editingPointer = null;
                _editingAction = null;
                SaveProfile();
                return true;
            default:
                return true;
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        UpdateViewportGeometry();
        // Real USB/Bluetooth mice are not consistent on Android: some vendors deliver mouse
        // button and drag packets through the touch channel instead of generic motion.
        if (IsHardwarePointer(e)) return HandleHardwareMotion(e);
        if (HandleSettingsTouch(e) || HandleLayoutEditTouch(e)) return true;
        var actionIndex = e.ActionIndex;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                var pointerId = e.GetPointerId(actionIndex);
                var x = e.GetX(actionIndex) / Width;
                var y = e.GetY(actionIndex) / Height;
                var gameplay = GameplayActive;
                // Visible overlay controls are checked first. The inventory key's hit rectangle
                // ends exactly where the ninth hotbar slot begins, so neither steals the other.
                var control = _profile.HitTest(x, y, gameplay);
                if (gameplay && control is null && TryGetHotbarSlot(x, y, out var slot))
                {
                    // A lost UP during an IME/window transition can leave the recycled ID in
                    // the router. The hotbar bypasses Router.Press, so cancel that stale gesture
                    // explicitly and invalidate any delayed long-press callback first.
                    _longPressTokens.Remove(pointerId);
                    _longPressIndicators.Remove(pointerId);
                    _router.Cancel(pointerId);
                    _hotbarPointers.Add(pointerId);
                    if (_sdl is null) NativeJvmHost.AddKey(49 + slot);
                    else _sdl.SelectSlot(slot);
                    return true;
                }
                var handled = _router.Press(pointerId, x, y, gameplay);
                if (handled && control is null)
                {
                    // Pointer IDs are recycled aggressively (usually ID 0 for each new
                    // single-finger gesture). Tie the delayed callback to this exact DOWN so
                    // an old tap cannot turn a later tap into an early long press.
                    var longPressToken = ++_nextLongPressToken;
                    _longPressTokens[pointerId] = longPressToken;
                    if (gameplay)
                        _longPressIndicators[pointerId] = new(x, y, SystemClock.UptimeMillis());
                    PostDelayed(() =>
                    {
                        if (!_longPressTokens.TryGetValue(pointerId, out var currentToken) ||
                            currentToken != longPressToken)
                            return;
                        if (!_router.BeginLongPress(pointerId))
                        {
                            _longPressTokens.Remove(pointerId);
                            _longPressIndicators.Remove(pointerId);
                        }
                        Invalidate();
                    }, LongPressDelayMs);
                }
                return handled;
            case MotionEventActions.Move:
                var moved = false;
                for (var i = 0; i < e.PointerCount; i++)
                {
                    var movingId = e.GetPointerId(i);
                    if (_hotbarPointers.Contains(movingId))
                    {
                        moved = true;
                        continue;
                    }
                    moved |= _router.Move(movingId, e.GetX(i) / Width, e.GetY(i) / Height);
                    if (!_router.IsLongPressCandidate(movingId))
                    {
                        _longPressTokens.Remove(movingId);
                        _longPressIndicators.Remove(movingId);
                    }
                }
                return moved;
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                var releasedId = e.GetPointerId(actionIndex);
                var releasedX = e.GetX(actionIndex) / Width;
                var releasedY = e.GetY(actionIndex) / Height;
                _longPressTokens.Remove(releasedId);
                _longPressIndicators.Remove(releasedId);
                if (_hotbarPointers.Remove(releasedId)) return true;
                // Capture this before Release removes the pointer. Overlay controls (especially
                // the keyboard button) must not be mistaken for taps on a Minecraft text field.
                var syncTextInput = _router.TargetsGameSurface(releasedId);
                var released = _router.Release(releasedId);
                if (released && syncTextInput && !GameplayActive)
                    PostDelayed(() => SyncSoftKeyboardAfterTap(releasedX, releasedY), 140);
                return released;
            case MotionEventActions.Cancel:
                _longPressTokens.Clear();
                _longPressIndicators.Clear();
                _hotbarPointers.Clear();
                _router.CancelAll();
                return true;
            default:
                return false;
        }
    }

    public override bool OnGenericMotionEvent(MotionEvent? e) =>
        HandleHardwareMotion(e) || base.OnGenericMotionEvent(e);

    public override bool OnHoverEvent(MotionEvent? e) =>
        HandleHardwareMotion(e) || base.OnHoverEvent(e);

    public override bool OnCapturedPointerEvent(MotionEvent? e) =>
        HandleHardwareMotion(e) || base.OnCapturedPointerEvent(e);

    public void SetAction(TouchControlAction action, bool pressed)
    {
        if (action == TouchControlAction.Settings)
        {
            if (pressed)
            {
                // Opening settings cancels every other finger. Releasing pending gestures here
                // would turn them into clicks or commit an inventory split behind the panel.
                _longPressTokens.Clear();
                _longPressIndicators.Clear();
                _hotbarPointers.Clear();
                _router.CancelAll();
                _settingsOpen = !_settingsOpen;
                _layoutEditing = false;
                Invalidate();
            }
            return;
        }
        if (action == TouchControlAction.Close)
            action = TouchControlAction.Menu;
        DispatchAction(action, pressed);
        if (action == TouchControlAction.Keyboard && pressed)
            PostDelayed(ShowKeyboard, 100);
    }

    public void CancelAction(TouchControlAction action)
    {
        if (action == TouchControlAction.Close)
            action = TouchControlAction.Menu;
        // SDL receives button edges immediately, so it still needs the matching release. The
        // GLFW snapshot bridge has a distinct cancellation edge that suppresses an unconsumed tap.
        if (_sdl is null) AndroidGameInputBridge.CancelAction(action);
        else _sdl.Action(action, false);
    }

    public void SetMovement(double x, double y) => DispatchMovement(x, y);

    public void AddLook(double x, double y) => DispatchLook(x, y);

    public void SetPointer(double x, double y, bool click)
        => DispatchPointer(x, y, click);

    private void DispatchAction(TouchControlAction action, bool pressed)
    {
        if (_sdl is null) AndroidGameInputBridge.SetAction(action, pressed);
        else _sdl.Action(action, pressed);
    }

    private void DispatchMovement(double x, double y)
    {
        if (_sdl is null) AndroidGameInputBridge.SetMovement(x, y);
        else _sdl.Move(x, y);
    }

    private void DispatchLook(double x, double y)
    {
        if (_sdl is null) AndroidGameInputBridge.AddLook(x, y);
        else _sdl.Look(x, y);
    }

    private void DispatchPointer(double x, double y, bool click)
    {
        if (_sdl is null) AndroidGameInputBridge.SetPointer(x, y, click);
        else _sdl.Pointer(x, y, click);
    }

    public void SplitInventoryStack(double x, double y, int count)
    {
        if (_sdl is null) NativeJvmHost.SplitInventoryStack(x, y, count);
        else _sdl.SplitInventoryStack(x, y, count);
    }

    public void HandleSystemBack()
    {
        CancelAllInput();
        if (_sdl is not null)
        {
            _sdl.KeyEvent(Keycode.Escape, true);
            _sdl.KeyEvent(Keycode.Escape, false);
            return;
        }

        NativeJvmHost.AddKey(GlfwKeyEscape);
    }

    public bool HandleHardwareKey(KeyEvent? e)
    {
        // Physical keys stay available in every touch layout. The dedicated external-input
        // profile controls visibility of touch buttons; it must not gate keyboard events.
        if (e is null || Visibility != ViewStates.Visible) return false;
        var pressed = e.Action == KeyEventActions.Down;
        if (e.Action is not (KeyEventActions.Down or KeyEventActions.Up)) return false;

        // Minecraft 26.3 owns an SDL window. Its keyboard queue must receive both the key-down
        // and key-up events; sending the old GLFW/JVM callbacks makes text appear to work while
        // leaving gameplay keys stuck or completely ignored.
        if (_sdl is not null)
        {
            _sdl.KeyEvent(e.KeyCode, pressed, pressed && e.RepeatCount > 0);
            if (pressed && e.UnicodeChar > 0 &&
                _hardwareTextInputGate.AcceptHardwareCodePoint((int)e.KeyCode,
                    e.UnicodeChar, e.DownTime, SystemClock.UptimeMillis(), e.RepeatCount))
                _sdl.CommitText(char.ConvertFromUtf32(e.UnicodeChar));
            return true;
        }

        // Menu and chat screens need ordinary key/character delivery before gameplay
        // bindings. Otherwise W/A/S/D and E/Q/T disappear from typed text.
        if (!GameplayActive)
        {
            if (!pressed) return GlfwKey(e.KeyCode) is not null || e.UnicodeChar > 0;
            // A physical key can be delivered both through Activity.DispatchKeyEvent and a
            // freshly-created InputConnection. Feed it through the view-lifetime gate here and
            // consume the Activity event; soft keyboards still use CommitText directly.
            var menuKey = GlfwKey(e.KeyCode);
            if (e.UnicodeChar > 0)
            {
                if (!_hardwareTextInputGate.AcceptHardwareCodePoint((int)e.KeyCode,
                        e.UnicodeChar, e.DownTime, SystemClock.UptimeMillis(), e.RepeatCount))
                    return true;
                // Printable keys still carry menu bindings. Minecraft needs E to close an
                // inventory and number keys to swap a hovered stack into the hotbar, while the
                // character callback is independently needed by chat and text fields.
                if (menuKey is { } mappedTextKey) NativeJvmHost.AddKey(mappedTextKey);
                NativeJvmHost.AddCodePoint(e.UnicodeChar);
                return true;
            }
            if (menuKey is not { } mappedMenuKey) return false;
            if (_hardwareTextInputGate.Accept((int)e.KeyCode, e.DownTime, e.RepeatCount))
                NativeJvmHost.AddKey(mappedMenuKey);
            return true;
        }

        if (pressed) _hardwareKeys.Add(e.KeyCode); else _hardwareKeys.Remove(e.KeyCode);

        if (IsMovementKey(e.KeyCode))
        {
            UpdateHardwareMovement();
            return true;
        }

        if (HardwareAction(e.KeyCode) is { } action)
        {
            DispatchHardwareActionTransition(action, pressed);
            return true;
        }

        if (GlfwKey(e.KeyCode) is not { } glfw) return false;
        if (pressed && e.RepeatCount == 0) NativeJvmHost.AddKey(glfw);
        return true;
    }

    public bool HandleHardwareMotion(MotionEvent? e)
    {
        if (e is null || Visibility != ViewStates.Visible || !IsHardwarePointer(e)) return false;

        if (e.ActionMasked == MotionEventActions.Scroll)
        {
            if (!GameplayActive) return false;
            var scroll = e.GetAxisValue(Axis.Vscroll);
            if (Math.Abs(scroll) > 0.01f)
            {
                var action = scroll > 0 ? TouchControlAction.PreviousSlot : TouchControlAction.NextSlot;
                DispatchAction(action, true);
                DispatchAction(action, false);
            }
            return true;
        }

        if (e.ActionMasked is MotionEventActions.ButtonPress or MotionEventActions.ButtonRelease or
            MotionEventActions.Down or MotionEventActions.Up)
        {
            var pressed = e.ActionMasked is MotionEventActions.ButtonPress or MotionEventActions.Down;
            // Several Android mouse drivers leave ActionButton empty for DOWN/UP. Those
            // actions represent the primary button.
            var actionButton = e.ActionMasked is MotionEventActions.Down or MotionEventActions.Up
                ? MotionEventButtonState.Primary : e.ActionButton;
            var action = actionButton switch
            {
                MotionEventButtonState.Primary => TouchControlAction.PrimaryAction,
                MotionEventButtonState.Secondary => TouchControlAction.SecondaryAction,
                MotionEventButtonState.Tertiary => TouchControlAction.Inventory,
                _ => (TouchControlAction?)null
            };
            // DOWN/UP can arrive without a preceding HOVER_MOVE (trackpads and ADB both do
            // this). Synchronize the menu cursor before delivering the click.
            if (!GameplayActive && Width > 0 && Height > 0)
                DispatchPointer(e.GetX() / Width, e.GetY() / Height, false);
            if (action is { } mapped)
                DispatchHardwareActionTransition(mapped, pressed);
            return true;
        }

        if (e.ActionMasked is not (MotionEventActions.HoverMove or MotionEventActions.Move)) return true;
        if (GameplayActive)
        {
            var dx = e.GetAxisValue(Axis.RelativeX);
            var dy = e.GetAxisValue(Axis.RelativeY);
            if (Math.Abs(dx) < 0.001f && Math.Abs(dy) < 0.001f && _hasMousePosition)
            {
                dx = e.GetX() - _lastMouseX;
                dy = e.GetY() - _lastMouseY;
            }
            if (Width > 0 && Height > 0 && (Math.Abs(dx) > 0.001f || Math.Abs(dy) > 0.001f))
                DispatchLook(dx / Width, dy / Height);
        }
        else if (Width > 0 && Height > 0)
        {
            DispatchPointer(e.GetX() / Width, e.GetY() / Height, false);
        }
        _lastMouseX = e.GetX();
        _lastMouseY = e.GetY();
        _hasMousePosition = true;
        return true;
    }

    private static bool IsHardwarePointer(MotionEvent e) =>
        e.IsFromSource(InputSourceType.Mouse) ||
        e.IsFromSource(InputSourceType.MouseRelative) ||
        e.IsFromSource(InputSourceType.Touchpad);

    public void UpdateHardwareCapture(bool hasWindowFocus)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
#pragma warning disable CA1416
        if (hasWindowFocus && Visibility == ViewStates.Visible &&
            _profile.Mode == TouchControlMode.ExternalKeyboardMouse &&
            GameplayActive)
        {
            RequestFocus();
            RequestPointerCapture();
        }
        else if (HasPointerCapture)
            ReleasePointerCapture();
#pragma warning restore CA1416
    }

    public void ReleaseHardwareInput()
    {
        _hardwareKeys.Clear();
        DispatchMovement(0, 0);
        foreach (var action in _hardwareActions.ToArray())
            DispatchAction(action, false);
        _hardwareActions.Clear();
        _sdl?.ReleaseAll();
        _hasMousePosition = false;
    }

    private void DispatchHardwareActionTransition(TouchControlAction action, bool pressed)
    {
        // Some Android mouse stacks report one physical click as both DOWN/UP and
        // BUTTON_PRESS/BUTTON_RELEASE. Only forward state transitions; forwarding the duplicate
        // release becomes a second click in both the GLFW and SDL bridges.
        var changed = pressed ? _hardwareActions.Add(action) : _hardwareActions.Remove(action);
        if (changed) DispatchAction(action, pressed);
    }

    public void CancelAllInput()
    {
        _longPressTokens.Clear();
        _longPressIndicators.Clear();
        _hotbarPointers.Clear();
        _router.CancelAll();
        ReleaseHardwareInput();
    }

    private static bool IsMovementKey(Keycode key) => key is Keycode.W or Keycode.A or Keycode.S or Keycode.D
        or Keycode.DpadUp or Keycode.DpadLeft or Keycode.DpadDown or Keycode.DpadRight;

    private void UpdateHardwareMovement()
    {
        var left = _hardwareKeys.Contains(Keycode.A) || _hardwareKeys.Contains(Keycode.DpadLeft);
        var right = _hardwareKeys.Contains(Keycode.D) || _hardwareKeys.Contains(Keycode.DpadRight);
        var forward = _hardwareKeys.Contains(Keycode.W) || _hardwareKeys.Contains(Keycode.DpadUp);
        var back = _hardwareKeys.Contains(Keycode.S) || _hardwareKeys.Contains(Keycode.DpadDown);
        DispatchMovement((right ? 1 : 0) - (left ? 1 : 0),
            (back ? 1 : 0) - (forward ? 1 : 0));
    }

    private static TouchControlAction? HardwareAction(Keycode key) => key switch
    {
        Keycode.Space => TouchControlAction.Jump,
        Keycode.ShiftLeft or Keycode.ShiftRight => TouchControlAction.Sneak,
        Keycode.CtrlLeft or Keycode.CtrlRight => TouchControlAction.Sprint,
        Keycode.E => TouchControlAction.Inventory,
        Keycode.Escape => TouchControlAction.Menu,
        Keycode.T => TouchControlAction.Keyboard,
        Keycode.Q => TouchControlAction.Drop,
        _ => null
    };

    private static int? GlfwKey(Keycode key) => key switch
    {
        >= Keycode.Num0 and <= Keycode.Num9 => 48 + key - Keycode.Num0,
        >= Keycode.A and <= Keycode.Z => 65 + key - Keycode.A,
        Keycode.Enter => 257,
        Keycode.Tab => 258,
        Keycode.Del => 259,
        Keycode.Insert => 260,
        Keycode.ForwardDel => 261,
        Keycode.DpadRight => 262,
        Keycode.DpadLeft => 263,
        Keycode.DpadDown => 264,
        Keycode.DpadUp => 265,
        Keycode.PageUp => 266,
        Keycode.PageDown => 267,
        Keycode.MoveHome => 268,
        Keycode.MoveEnd => 269,
        Keycode.F1 => 290,
        Keycode.F2 => 291,
        Keycode.F3 => 292,
        Keycode.F4 => 293,
        Keycode.F5 => 294,
        Keycode.F6 => 295,
        Keycode.F7 => 296,
        Keycode.F8 => 297,
        Keycode.F9 => 298,
        Keycode.F10 => 299,
        Keycode.F11 => 300,
        Keycode.F12 => 301,
        Keycode.Slash => 47,
        Keycode.Minus => 45,
        Keycode.Equals => 61,
        Keycode.Comma => 44,
        Keycode.Period => 46,
        Keycode.Semicolon => 59,
        Keycode.Apostrophe => 39,
        Keycode.LeftBracket => 91,
        Keycode.RightBracket => 93,
        Keycode.Backslash => 92,
        _ => null
    };

    private void ShowKeyboard()
    {
        _manualKeyboardRequested = true;
        RequestFocus();
        var input = (InputMethodManager?)Context!.GetSystemService(Context.InputMethodService);
        input?.RestartInput(this);
        // A tablet or emulator can advertise a hardware keyboard even when the player expects
        // touch input. The chat/command button must therefore request the soft keyboard explicitly.
        input?.ShowSoftInput(this, ShowFlags.Forced);
    }

    private void SyncSoftKeyboardAfterTap(double x, double y)
    {
        // SDL owns text-field focus for the 26.3 client and its Java bridge deliberately has no
        // coordinate hit-test. Once SDL reports active text input, keep/restart the IME; when it
        // does not, let the normal focus transition hide it rather than treating every tap as an
        // authoritative miss and closing a keyboard that SDL has just opened asynchronously.
        if (_sdl is not null)
        {
            if (_sdl.TextInputActive) ShowKeyboard();
            return;
        }

        var targeted = NativeJvmHost.TextInputAt(x, y);
        if (targeted)
        {
            ShowKeyboard();
            return;
        }

        HideKeyboard();
    }

    private void HideKeyboard()
    {
        _manualKeyboardRequested = false;
        var input = (InputMethodManager?)Context!.GetSystemService(Context.InputMethodService);
        input?.HideSoftInputFromWindow(WindowToken, HideSoftInputFlags.None);
        // ShowFlags.Forced is needed on tablets that report a hardware keyboard, but it also
        // makes Android eager to reopen the IME while this View remains focused. Drop Android
        // view focus after a non-editor tap; ShowKeyboard restores it on the next real editor tap.
        ClearFocus();
    }

    private sealed class JayInputConnection : BaseInputConnection
    {
        private const int GlfwKeyEnter = 257;
        private const int GlfwKeyBackspace = 259;
        private readonly TouchControlOverlay _owner;

        public JayInputConnection(TouchControlOverlay owner) : base(owner, false)
        {
            _owner = owner;
        }

        public override bool CommitText(Java.Lang.ICharSequence? text, int newCursorPosition)
        {
            if (text?.ToString() is { } value)
            {
                if (_owner._sdl is not null)
                {
                    _owner._sdl.CommitText(value);
                    return true;
                }
                var runes = value.EnumerateRunes().ToArray();
                if (runes.Length == 1)
                {
                    var codePoint = runes[0].Value;
                    if (_owner._hardwareTextInputGate.AcceptCommittedCodePoint(codePoint,
                            SystemClock.UptimeMillis()))
                        NativeJvmHost.AddCodePoint(codePoint);
                }
                else
                {
                    foreach (var rune in runes) NativeJvmHost.AddCodePoint(rune.Value);
                }
            }
            return true;
        }

        public override bool DeleteSurroundingText(int beforeLength, int afterLength)
        {
            if (_owner._sdl is not null)
            {
                for (var i = 0; i < Math.Max(1, beforeLength); i++) _owner._sdl.Backspace();
                return true;
            }
            for (var i = 0; i < Math.Max(1, beforeLength); i++) NativeJvmHost.AddKey(GlfwKeyBackspace);
            return true;
        }

        public override bool PerformEditorAction(ImeAction actionCode)
        {
            if (_owner._sdl is not null)
            {
                _owner._sdl.Enter();
                return true;
            }
            NativeJvmHost.AddKey(GlfwKeyEnter);
            return true;
        }

        public override bool SendKeyEvent(KeyEvent? e)
        {
            if (e is null) return true;
            if (_owner._sdl is not null)
            {
                if (e.Action is KeyEventActions.Down or KeyEventActions.Up)
                    _owner._sdl.KeyEvent(e.KeyCode, e.Action == KeyEventActions.Down,
                        e.Action == KeyEventActions.Down && e.RepeatCount > 0);
                // Some IMEs express printable text only as SendKeyEvent and never follow it
                // with CommitText. Preserve that character for SDL while the view-lifetime
                // gate suppresses a duplicate Activity delivery or a correlated CommitText.
                if (e.Action == KeyEventActions.Down && e.UnicodeChar > 0 &&
                    _owner._hardwareTextInputGate.AcceptHardwareCodePoint((int)e.KeyCode,
                        e.UnicodeChar, e.DownTime, SystemClock.UptimeMillis(), e.RepeatCount))
                    _owner._sdl.CommitText(char.ConvertFromUtf32(e.UnicodeChar));
                return true;
            }
            if (e.Action != KeyEventActions.Down) return true;
            if (e.KeyCode == Keycode.Del) NativeJvmHost.AddKey(GlfwKeyBackspace);
            else if (e.KeyCode == Keycode.Enter) NativeJvmHost.AddKey(GlfwKeyEnter);
            else if (e.UnicodeChar > 0)
            {
                var handledAt = SystemClock.UptimeMillis();
                if (_owner._hardwareTextInputGate.AcceptHardwareCodePoint((int)e.KeyCode,
                        e.UnicodeChar, e.DownTime, handledAt, e.RepeatCount))
                    NativeJvmHost.AddCodePoint(e.UnicodeChar);
            }
            // Soft keyboards normally use CommitText; physical keyboards can instead arrive as
            // SendKeyEvent. Both paths feed the character callback, but never for one event.
            return true;
        }
    }

    private readonly record struct LongPressIndicator(double X, double Y, long DownAt);
}
