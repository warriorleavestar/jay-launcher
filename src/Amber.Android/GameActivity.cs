using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Amber.Core.Localization;
using Amber.Core.Install;
using Amber.Mobile;
using System.Runtime.Versioning;
using System.Text;

namespace Amber.Android;

[Activity(
    Label = "Jay Game",
    Process = ":game",
    Theme = "@style/MyTheme.NoActionBar",
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    // One game screen per game process: opening the game again brings the running one to the front.
    LaunchMode = LaunchMode.SingleTask,
    // The running game cannot be recreated, so keep the activity across keyboard and window changes.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
                           ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class GameActivity : Activity, ISurfaceHolderCallback
{
    public const string ServerAddressExtra = "nz.work.minecraft.jay.SERVER_ADDRESS";
    private const int GlfwKeyEscape = 256;
    private CancellationTokenSource? _runtimeSetup;
    private CancellationTokenSource? _gameSetup;
    private Java.Lang.Object? _backCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);

        var root = new FrameLayout(this);
        var surface = new SurfaceView(this);
        surface.Holder!.AddCallback(this);
        root.AddView(surface, MatchParent());
        root.AddView(new TouchControlOverlay(this), MatchParent());

        if (AndroidEmbeddedGame.IsRunning)
        {
            // The screen was recreated under a running game; it continues on the new surface.
        }
        else if (AndroidEmbeddedGame.HasStarted)
            AddGameEnded(root, L.T("mobile.game.ended"));
        else if (!AndroidRuntimeStore.IsInstalled(this))
            AddRuntimeSetup(root);
        else
            AddGameSetup(root);

        SetContentView(root);
        EnterImmersiveMode();
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) RegisterBackCallback();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        // Before the game starts, use the latest server address; a running game keeps its own.
        if (intent is not null && !AndroidEmbeddedGame.HasStarted) Intent = intent;
    }

    [SupportedOSPlatform("android33.0")]
    private void RegisterBackCallback()
    {
        var callback = new BackCallback(HandleBack);
        // 0 is OnBackInvokedDispatcher.PRIORITY_DEFAULT.
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, callback);
        _backCallback = callback;
    }

    [SupportedOSPlatform("android33.0")]
    private void UnregisterBackCallback()
    {
        if (_backCallback is global::Android.Window.IOnBackInvokedCallback callback)
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(callback);
        _backCallback?.Dispose();
        _backCallback = null;
    }

#pragma warning disable CS0672, CA1422 // Android 12 and older still deliver Back here.
    public override void OnBackPressed() => HandleBack();
#pragma warning restore CS0672, CA1422

    private void HandleBack()
    {
        switch (AndroidEmbeddedGame.State.BackAction)
        {
            case EmbeddedGameBackAction.SendEscape:
                // Back acts like Esc in the game (pause menu, close screens) instead of leaving the
                // screen while the game keeps running in the background.
                NativeJvmHost.AddKey(GlfwKeyEscape);
                break;
            case EmbeddedGameBackAction.EndProcess:
                EndGameProcess();
                break;
            default:
                Finish();
                break;
        }
    }

    /// <summary>
    /// Leaves the game screen and ends the game process. The embedded JVM can be created only once per
    /// process, so the next game starts in a fresh process.
    /// </summary>
    private void EndGameProcess()
    {
        Finish();
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    private void AddGameEnded(FrameLayout root, string message)
    {
        var status = new TextView(this) { Text = message, Gravity = GravityFlags.Center };
        status.SetTextColor(Color.White);
        var close = new Button(this) { Text = L.T("mobile.game.close") };
        close.Click += (_, _) => EndGameProcess();

        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetGravity(GravityFlags.Center);
        content.SetBackgroundColor(Color.Argb(225, 20, 24, 30));
        content.SetPadding(40, 32, 40, 32);
        content.AddView(status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        content.AddView(close, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center)
        {
            LeftMargin = 64,
            RightMargin = 64
        });
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) EnterImmersiveMode();
    }

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
        _runtimeSetup?.Cancel();
        _runtimeSetup?.Dispose();
        _runtimeSetup = null;
        _gameSetup?.Cancel();
        _gameSetup?.Dispose();
        _gameSetup = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) UnregisterBackCallback();
        base.OnDestroy();
    }

    private void AddRuntimeSetup(FrameLayout root)
    {
        var status = new TextView(this) { Text = L.T("mobile.runtime.missing"), Gravity = GravityFlags.Center };
        status.SetTextColor(Color.White);

        var progress = new ProgressBar(this, null, 0, Resource.Style.JayProgressBarHorizontal)
        {
            Max = 100,
            Visibility = ViewStates.Gone
        };
        var install = new Button(this) { Text = L.T("mobile.runtime.install") };
        var cancel = new Button(this) { Text = L.T("mobile.runtime.cancel"), Visibility = ViewStates.Gone };
        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.Center);
        buttons.AddView(install);
        buttons.AddView(cancel);

        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetGravity(GravityFlags.Center);
        content.SetBackgroundColor(Color.Argb(225, 20, 24, 30));
        content.SetPadding(40, 32, 40, 32);
        content.AddView(status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        content.AddView(progress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        content.AddView(buttons, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var panel = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center)
        {
            LeftMargin = 64,
            RightMargin = 64
        };
        root.AddView(content, panel);

        install.Click += async (_, _) =>
        {
            var descriptor = AndroidRuntimeCatalog.ForCurrentProcess();
            if (descriptor is null)
            {
                status.Text = L.T("mobile.runtime.unsupported");
                return;
            }

            _runtimeSetup?.Dispose();
            _runtimeSetup = new CancellationTokenSource();
            install.Enabled = false;
            cancel.Visibility = ViewStates.Visible;
            progress.Visibility = ViewStates.Visible;
            progress.Progress = 0;

            var acceptingProgress = true;
            var report = new Progress<RuntimeInstallProgress>(value =>
            {
                if (!acceptingProgress) return;
                var percent = value.Total <= 0 ? 0 : (int)Math.Clamp(value.Completed * 100 / value.Total, 0, 100);
                progress.Progress = percent;
                status.Text = value.Phase switch
                {
                    RuntimeInstallPhase.Downloading => L.F("mobile.runtime.downloading", percent),
                    RuntimeInstallPhase.Extracting => L.F("mobile.runtime.extracting", percent),
                    RuntimeInstallPhase.Verifying => L.F("mobile.runtime.verifying", percent),
                    _ => L.T("mobile.runtime.activating")
                };
            });

            try
            {
                await AndroidRuntimeStore.InstallAsync(this, descriptor, report, _runtimeSetup.Token);
                acceptingProgress = false;
                progress.Progress = 100;
                status.Text = L.T("mobile.runtime.ready");
                install.Visibility = ViewStates.Gone;
                root.RemoveView(content);
                AddGameSetup(root);
            }
            catch (System.OperationCanceledException)
            {
                acceptingProgress = false;
                status.Text = L.T("mobile.runtime.missing");
                progress.Visibility = ViewStates.Gone;
            }
            catch (Exception exception)
            {
                acceptingProgress = false;
                global::Android.Util.Log.Error("JayRuntime", exception.ToString());
                status.Text = L.F("mobile.runtime.failed", exception.Message);
            }
            finally
            {
                install.Enabled = true;
                cancel.Visibility = ViewStates.Gone;
            }
        };
        cancel.Click += (_, _) => _runtimeSetup?.Cancel();
    }

    private void AddGameSetup(FrameLayout root)
    {
        var preferences = GetPreferences(FileCreationMode.Private)!;
        var status = new TextView(this) { Text = L.T("mobile.game.setup"), Gravity = GravityFlags.Center };
        status.SetTextColor(Color.White);
        var playerName = new EditText(this)
        {
            Hint = L.T("mobile.game.player_name"),
            Text = preferences.GetString("offline-player", "Player")
        };
        playerName.SetTextColor(Color.White);
        playerName.SetHintTextColor(Color.LightGray);
        playerName.SetSingleLine(true);
        var progress = new ProgressBar(this, null, 0, Resource.Style.JayProgressBarHorizontal)
        {
            Max = 100,
            Visibility = ViewStates.Gone
        };
        var play = new Button(this) { Text = L.F("mobile.game.install_play", AndroidEmbeddedGame.FirstPlayableVersion) };
        var cancel = new Button(this) { Text = L.T("mobile.runtime.cancel"), Visibility = ViewStates.Gone };
        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.Center);
        buttons.AddView(play);
        buttons.AddView(cancel);

        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetGravity(GravityFlags.Center);
        content.SetBackgroundColor(Color.Argb(225, 20, 24, 30));
        content.SetPadding(40, 32, 40, 32);
        content.AddView(status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        content.AddView(playerName, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        content.AddView(progress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        content.AddView(buttons, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        var panel = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center)
        {
            LeftMargin = 64,
            RightMargin = 64
        };
        root.AddView(content, panel);

        play.Click += async (_, _) =>
        {
            _gameSetup?.Dispose();
            _gameSetup = new CancellationTokenSource();
            play.Enabled = false;
            playerName.Enabled = false;
            cancel.Visibility = ViewStates.Visible;
            progress.Visibility = ViewStates.Visible;
            var report = new Progress<AndroidGameInstallProgress>(value =>
            {
                var percent = value.Total <= 0 ? 0 : (int)Math.Clamp(value.Completed * 100L / value.Total, 0, 100);
                progress.Progress = percent;
                status.Text = value.Stage switch
                {
                    InstallStage.Manifest or InstallStage.VersionJson => L.T("mobile.game.fetching_manifest"),
                    InstallStage.AssetIndex => L.T("mobile.game.fetching_assets"),
                    InstallStage.Files => L.F("mobile.game.downloading", value.Completed, value.Total),
                    _ => L.T("mobile.game.preparing")
                };
            });
            try
            {
                var name = playerName.Text?.Trim() ?? "";
                var prepared = await AndroidEmbeddedGame.PrepareAsync(this, name,
                    Intent?.GetStringExtra(ServerAddressExtra), report, _gameSetup.Token);
                var editor = preferences.Edit();
                editor?.PutString("offline-player", name);
                editor?.Apply();
                root.RemoveView(content);
                await Task.Run(() => AndroidEmbeddedGame.Run(prepared), _gameSetup.Token);
                // The game ended without ending the process. It cannot start again in this process.
                EndGameProcess();
            }
            catch (System.OperationCanceledException)
            {
                status.Text = L.T("mobile.game.setup");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException
                                               or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                if (AndroidEmbeddedGame.HasStarted)
                {
                    // The JVM exists already, so Play cannot work again here: offer only closing.
                    if (content.Parent is not null) root.RemoveView(content);
                    AddGameEnded(root, L.F("mobile.game.failed", exception.Message));
                    return;
                }
                status.Text = L.F("mobile.game.failed", exception.Message);
                if (content.Parent is null) root.AddView(content, panel);
            }
            finally
            {
                play.Enabled = true;
                playerName.Enabled = true;
                cancel.Visibility = ViewStates.Gone;
            }
        };
        cancel.Click += (_, _) => _gameSetup?.Cancel();
    }

    public void SurfaceCreated(ISurfaceHolder holder) => NativeJvmHost.SetSurface(holder.Surface);

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) =>
        NativeJvmHost.SetSurface(holder.Surface);

    public void SurfaceDestroyed(ISurfaceHolder holder) => NativeJvmHost.SetSurface(null);

    private static FrameLayout.LayoutParams MatchParent() => new(
        ViewGroup.LayoutParams.MatchParent,
        ViewGroup.LayoutParams.MatchParent);

    [SupportedOSPlatform("android33.0")]
    private sealed class BackCallback(Action onBack) : Java.Lang.Object, global::Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => onBack();
    }
}

internal sealed class TouchControlOverlay : View, ITouchInputSink
{
    private const long LongPressDelayMs = 340;
    private const long LongPressRingMs = 900;
    private const long StatePollMs = 100;
    private readonly Paint _fill = new() { AntiAlias = true, Color = Color.Argb(142, 48, 53, 59) };
    private readonly Paint _activeFill = new() { AntiAlias = true, Color = Color.Argb(218, 105, 112, 120) };
    private readonly Paint _buttonFace = new() { AntiAlias = false, Color = Color.Argb(82, 76, 82, 86) };
    private readonly Paint _buttonPressed = new() { AntiAlias = false, Color = Color.Argb(145, 142, 146, 150) };
    private readonly Paint _buttonBorder = new() { AntiAlias = false, Color = Color.Argb(145, 225, 228, 232), StrokeWidth = 2 };
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
    private bool _settingsOpen;
    private bool _layoutEditing;
    private int? _editingPointer;
    private TouchControlAction? _editingAction;
    private float _controlOpacity = 0.62f;
    private int _guiScaleOption = MinecraftHotbar.DefaultGuiScaleOption;
    private bool _lastGameplayControlsActive;
    private bool _polling;
    private bool _polledGameplay;
    private Java.Lang.Runnable? _poll;

    public TouchControlOverlay(Context context) : base(context)
    {
        _preferences = context.GetSharedPreferences("jay-touch-controls", FileCreationMode.Private)!;
        LoadProfile();
        _router = new TouchInputRouter(this, _profile);
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
        var gameplayControlsActive = NativeJvmHost.GameplayControlsActive;
        // The GUI scale can only change in a menu, so re-read it whenever play resumes.
        if (gameplayControlsActive && !_lastGameplayControlsActive) RefreshHotbar();
        _lastGameplayControlsActive = gameplayControlsActive;
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
                    splitGlyph: !gameplayControlsActive && control.Action == TouchControlAction.SecondaryAction);
        }
        DrawLongPressIndicators(canvas);
        if (_profile.Mode == TouchControlMode.JoystickAndCrosshair && gameplayControlsActive)
            DrawCrosshair(canvas);
        if (_settingsOpen)
            DrawSettings(canvas);
        else if (_layoutEditing)
            DrawEditGuide(canvas);
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        _polling = true;
        _poll ??= new Java.Lang.Runnable(PollGameState);
        RemoveCallbacks(_poll);
        PostDelayed(_poll, StatePollMs);
    }

    protected override void OnDetachedFromWindow()
    {
        _polling = false;
        if (_poll is not null) RemoveCallbacks(_poll);
        base.OnDetachedFromWindow();
    }

    /// <summary>
    /// Redraws only when something on screen can have changed without a touch: the game switched
    /// between play and a menu, or a long-press ring is animating. Touches redraw directly.
    /// </summary>
    private void PollGameState()
    {
        if (!_polling) return;
        var gameplay = NativeJvmHost.GameplayControlsActive;
        if (gameplay != _polledGameplay || _longPressIndicators.Count > 0) Invalidate();
        _polledGameplay = gameplay;
        if (_poll is not null) PostDelayed(_poll, StatePollMs);
    }

    private void DrawMovementPad(Canvas canvas, float x, float y, float radius)
    {
        if (_profile.Mode == TouchControlMode.DPadAndTap)
        {
            DrawDirectionPad(canvas, x, y, radius);
            return;
        }
        var fill = _router.IsPressed(TouchControlAction.Move) ? _activeFill : _fill;
        canvas.DrawCircle(x, y, radius, fill);
        canvas.DrawCircle(x, y, radius, _stroke);
        var (moveX, moveY) = _router.Movement;
        var knobX = x + (float)moveX * radius * 0.56f;
        var knobY = y + (float)moveY * radius * 0.56f;
        canvas.DrawCircle(knobX, knobY, radius * 0.34f,
            _router.IsPressed(TouchControlAction.Move) ? _activeFill : _fill);
        canvas.DrawCircle(knobX, knobY, radius * 0.34f, _stroke);
    }

    private void DrawDirectionPad(Canvas canvas, float x, float y, float radius)
    {
        var cell = radius * 0.62f;
        for (var row = -1; row <= 1; row++)
        for (var col = -1; col <= 1; col++)
        {
            if ((row != 0 && col != 0) || (row == 0 && col == 0)) continue;
            var bounds = new RectF(x + col * cell - cell * 0.48f, y + row * cell - cell * 0.48f,
                x + col * cell + cell * 0.48f, y + row * cell + cell * 0.48f);
            canvas.DrawRect(bounds, _router.IsPressed(TouchControlAction.Move) ? _buttonPressed : _buttonFace);
            canvas.DrawRect(bounds, _buttonBorder);
            DrawDirectionGlyph(canvas, x + col * cell, y + row * cell, col, row, cell * 0.28f);
        }
    }

    private void DrawDirectionGlyph(Canvas canvas, float x, float y, int dx, int dy, float size)
    {
        _glyph.SetStyle(Paint.Style.Stroke);
        _glyph.StrokeWidth = Math.Max(3, size * 0.22f);
        var path = new global::Android.Graphics.Path();
        if (dx != 0)
        {
            canvas.DrawLine(x - dx * size * 0.70f, y, x + dx * size * 0.58f, y, _glyph);
            path.MoveTo(x + dx * size * 0.66f, y);
            path.LineTo(x + dx * size * 0.12f, y - size * 0.50f);
            path.MoveTo(x + dx * size * 0.66f, y);
            path.LineTo(x + dx * size * 0.12f, y + size * 0.50f);
        }
        else
        {
            canvas.DrawLine(x, y - dy * size * 0.70f, x, y + dy * size * 0.58f, _glyph);
            path.MoveTo(x, y + dy * size * 0.66f);
            path.LineTo(x - size * 0.50f, y + dy * size * 0.12f);
            path.MoveTo(x, y + dy * size * 0.66f);
            path.LineTo(x + size * 0.50f, y + dy * size * 0.12f);
        }
        canvas.DrawPath(path, _glyph);
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
        canvas.DrawText(L.T("mobile.touch.edit_hint"), Width * 0.5f, Height * 0.160f, _hint);
        canvas.DrawRect(Width * 0.87f, Height * 0.02f, Width * 0.98f, Height * 0.10f, _activeFill);
        canvas.DrawText(L.T("mobile.touch.done"), Width * 0.925f, Height * 0.075f, _hint);
    }

    private void DrawSettings(Canvas canvas)
    {
        var left = Width * 0.60f;
        var right = Width * 0.97f;
        canvas.DrawRect(0, 0, Width, Height, _scrim);
        canvas.DrawRect(left, Height * 0.08f, right, Height * 0.96f, _panel);
        _hint.TextSize = Math.Max(17, Math.Min(Width, Height) * 0.021f);
        canvas.DrawText(L.T("mobile.touch.settings"), (left + right) / 2, Height * 0.14f, _hint);
        DrawSettingRow(canvas, L.T("mobile.touch.mode.joystick_tap"), 0.23f, _profile.Mode == TouchControlMode.JoystickAndTap);
        DrawSettingRow(canvas, L.T("mobile.touch.mode.dpad_tap"), 0.33f, _profile.Mode == TouchControlMode.DPadAndTap);
        DrawSettingRow(canvas, L.T("mobile.touch.mode.joystick_crosshair"), 0.43f, _profile.Mode == TouchControlMode.JoystickAndCrosshair);
        DrawStepper(canvas, L.F("mobile.touch.size", (int)Math.Round(_profile.Scale * 100)), 0.56f);
        DrawStepper(canvas, L.F("mobile.touch.opacity", (int)Math.Round(_controlOpacity * 100)), 0.66f);
        DrawSettingRow(canvas, _profile.ForcedSprint ? L.T("mobile.touch.sprint_on") : L.T("mobile.touch.sprint_off"), 0.75f,
            _profile.ForcedSprint);
        DrawPanelButton(canvas, L.T("mobile.touch.reset"), 0.615f, 0.83f, 0.77f, 0.90f);
        DrawPanelButton(canvas, L.T("mobile.touch.edit_layout"), 0.80f, 0.83f, 0.955f, 0.90f);
        DrawPanelButton(canvas, L.T("mobile.touch.done"), 0.70f, 0.91f, 0.87f, 0.955f);
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
        _buttonFace.Color = WithAlpha(Color.Argb(82, 76, 82, 86), _controlOpacity);
        _buttonPressed.Color = WithAlpha(Color.Argb(145, 142, 146, 150), _controlOpacity);
        _buttonBorder.Color = WithAlpha(Color.Argb(145, 225, 228, 232), Math.Max(0.45f, _controlOpacity));
    }

    private void DrawLongPressIndicators(Canvas canvas)
    {
        var now = SystemClock.UptimeMillis();
        var scale = Math.Min(Width, Height);
        var radius = scale * 0.035f;
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

    private void DrawActionButton(Canvas canvas, TouchControlDefinition control,
        float x, float y, float radius, bool splitGlyph)
    {
        var compact = control.Action is TouchControlAction.PreviousSlot or TouchControlAction.NextSlot;
        var utility = control.Action is TouchControlAction.Menu or TouchControlAction.Keyboard or
            TouchControlAction.Inventory or TouchControlAction.Drop;
        var faceScale = compact ? 0.78f : utility ? (float)TouchControlLayout.UtilityFaceScale : 0.92f;
        var halfWidth = radius * faceScale;
        var halfHeight = radius * faceScale;
        var bounds = new RectF((float)Math.Round(x - halfWidth), (float)Math.Round(y - halfHeight),
            (float)Math.Round(x + halfWidth), (float)Math.Round(y + halfHeight));
        _buttonBorder.SetStyle(Paint.Style.Stroke);
        canvas.DrawRect(bounds, _router.IsPressed(control.Action) ? _buttonPressed : _buttonFace);
        canvas.DrawRect(bounds, _buttonBorder);
        DrawGlyph(canvas, control.Action, x, y, Math.Min(halfWidth, halfHeight) * 0.68f, control.Label, splitGlyph);
    }

    private void DrawGlyph(Canvas canvas, TouchControlAction action, float x, float y, float size, string fallback,
        bool splitGlyph)
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
                if (splitGlyph)
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
        MinecraftHotbar.TryGetSlot(x, y, Width, Height, _guiScaleOption, out slot);

    /// <summary>Re-reads the game's GUI scale and places the hotbar for the current surface size.</summary>
    private void RefreshHotbar()
    {
        string? options = null;
        try
        {
            options = File.ReadAllText(Path.Combine(Context!.FilesDir!.AbsolutePath, "minecraft", "options.txt"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _guiScaleOption = MinecraftHotbar.ReadGuiScaleOption(options);
        _profile.HotbarBounds = Width > 0 && Height > 0
            ? MinecraftHotbar.Bounds(Width, Height, _guiScaleOption)
            : null;
    }

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
        _profile.AspectRatio = height > 0 ? (double)width / height : 1;
        RefreshHotbar();
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q) return;
#pragma warning disable CA1416
        SystemGestureExclusionRects = [new Rect(0, Math.Max(0, height - 200), width, height)];
#pragma warning restore CA1416
    }

    private void LoadProfile()
    {
        _profile.Mode = Enum.TryParse<TouchControlMode>(_preferences.GetString("mode", null), out var mode)
            ? mode : TouchControlMode.JoystickAndTap;
        _profile.Scale = Math.Clamp(_preferences.GetFloat("scale", 1f), 0.70, 1.35);
        _profile.ForcedSprint = _preferences.GetBoolean("forced-sprint", true);
        _controlOpacity = Math.Clamp(_preferences.GetFloat("opacity", 0.62f), 0.30f, 1f);
        foreach (var modeValue in Enum.GetValues<TouchControlMode>())
        foreach (var action in Enum.GetValues<TouchControlAction>())
        {
            var x = _preferences.GetFloat($"position-{modeValue}-{action}-x", float.NaN);
            var y = _preferences.GetFloat($"position-{modeValue}-{action}-y", float.NaN);
            if (!float.IsNaN(x) && !float.IsNaN(y)) _profile.SetPosition(modeValue, action, x, y);
        }
    }

    private void SaveProfile()
    {
        using var editor = _preferences.Edit()!;
        editor.PutString("mode", _profile.Mode.ToString());
        editor.PutFloat("scale", (float)_profile.Scale);
        editor.PutFloat("opacity", _controlOpacity);
        editor.PutBoolean("forced-sprint", _profile.ForcedSprint);
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
        if (y is > 0.19f and < 0.27f) _profile.Mode = TouchControlMode.JoystickAndTap;
        else if (y is > 0.29f and < 0.37f) _profile.Mode = TouchControlMode.DPadAndTap;
        else if (y is > 0.39f and < 0.47f) _profile.Mode = TouchControlMode.JoystickAndCrosshair;
        else if (y is > 0.52f and < 0.60f && x < 0.70f) _profile.Scale = Math.Max(0.70, _profile.Scale - 0.05);
        else if (y is > 0.52f and < 0.60f && x > 0.87f) _profile.Scale = Math.Min(1.35, _profile.Scale + 0.05);
        else if (y is > 0.62f and < 0.70f && x < 0.70f) _controlOpacity = Math.Max(0.30f, _controlOpacity - 0.05f);
        else if (y is > 0.62f and < 0.70f && x > 0.87f) _controlOpacity = Math.Min(1f, _controlOpacity + 0.05f);
        else if (y is > 0.71f and < 0.79f) _profile.ForcedSprint = !_profile.ForcedSprint;
        else if (y is > 0.83f and < 0.90f && x < 0.78f)
        {
            _profile.ResetPositions();
            _profile.Scale = 1;
            _controlOpacity = 0.62f;
            _profile.ForcedSprint = true;
        }
        else if (y is > 0.83f and < 0.90f && x > 0.78f)
        {
            _settingsOpen = false;
            _layoutEditing = true;
        }
        else if (y > 0.90f)
            _settingsOpen = false;
        SaveProfile();
        Invalidate();
        return true;
    }

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
        if (HandleSettingsTouch(e) || HandleLayoutEditTouch(e)) return true;
        // Pressed keys and the joystick knob follow the finger.
        Invalidate();
        var actionIndex = e.ActionIndex;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                var pointerId = e.GetPointerId(actionIndex);
                var x = e.GetX(actionIndex) / Width;
                var y = e.GetY(actionIndex) / Height;
                var gameplay = NativeJvmHost.GameplayControlsActive;
                if (gameplay && TryGetHotbarSlot(x, y, out var slot))
                {
                    _hotbarPointers.Add(pointerId);
                    NativeJvmHost.AddKey(49 + slot);
                    return true;
                }
                var handled = _router.Press(pointerId, x, y, gameplay);
                if (handled && gameplay && _profile.HitTest(x, y) is null)
                {
                    _longPressIndicators[pointerId] = new(x, y, SystemClock.UptimeMillis());
                    PostDelayed(() =>
                    {
                        if (!_router.BeginLongPress(pointerId))
                            _longPressIndicators.Remove(pointerId);
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
                        _longPressIndicators.Remove(movingId);
                }
                return moved;
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                var releasedId = e.GetPointerId(actionIndex);
                if (_hotbarPointers.Remove(releasedId)) return true;
                _longPressIndicators.Remove(releasedId);
                return _router.Release(releasedId);
            case MotionEventActions.Cancel:
                _longPressIndicators.Clear();
                _hotbarPointers.Clear();
                _router.ReleaseAll();
                return true;
            default:
                return false;
        }
    }

    public void SetAction(TouchControlAction action, bool pressed)
    {
        if (action == TouchControlAction.Settings)
        {
            if (pressed)
            {
                _router.ReleaseAll();
                _settingsOpen = !_settingsOpen;
                _layoutEditing = false;
                Invalidate();
            }
            return;
        }
        AndroidGameInputBridge.SetAction(action, pressed);
        if (action == TouchControlAction.Keyboard && pressed)
            PostDelayed(ShowKeyboard, 100);
    }

    public void SetMovement(double x, double y) => AndroidGameInputBridge.SetMovement(x, y);

    public void AddLook(double x, double y) => AndroidGameInputBridge.AddLook(x, y);

    public void SetPointer(double x, double y, bool click) => AndroidGameInputBridge.SetPointer(x, y, click);

    private void ShowKeyboard()
    {
        RequestFocus();
        var input = (InputMethodManager?)Context!.GetSystemService(Context.InputMethodService);
        input?.RestartInput(this);
        // A tablet or emulator can advertise a hardware keyboard even when the player expects
        // touch input. The chat/command button must therefore request the soft keyboard explicitly.
        input?.ShowSoftInput(this, ShowFlags.Forced);
    }

    private sealed class JayInputConnection(View target) : BaseInputConnection(target, false)
    {
        private const int GlfwKeyEnter = 257;
        private const int GlfwKeyBackspace = 259;

        public override bool CommitText(Java.Lang.ICharSequence? text, int newCursorPosition)
        {
            if (text?.ToString() is { } value)
                foreach (var rune in value.EnumerateRunes()) NativeJvmHost.AddCodePoint(rune.Value);
            return true;
        }

        public override bool DeleteSurroundingText(int beforeLength, int afterLength)
        {
            for (var i = 0; i < Math.Max(1, beforeLength); i++) NativeJvmHost.AddKey(GlfwKeyBackspace);
            return true;
        }

        public override bool PerformEditorAction(ImeAction actionCode)
        {
            NativeJvmHost.AddKey(GlfwKeyEnter);
            return true;
        }

        public override bool SendKeyEvent(KeyEvent? e)
        {
            if (e?.Action != KeyEventActions.Down) return true;
            if (e.KeyCode == Keycode.Del) NativeJvmHost.AddKey(GlfwKeyBackspace);
            else if (e.KeyCode == Keycode.Enter) NativeJvmHost.AddKey(GlfwKeyEnter);
            else if (e.UnicodeChar > 0) NativeJvmHost.AddCodePoint(e.UnicodeChar);
            return true;
        }
    }

    private readonly record struct LongPressIndicator(double X, double Y, long DownAt);
}
