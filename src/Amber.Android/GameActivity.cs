using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Amber.Core.Localization;
using Amber.Core.Install;
using Amber.Mobile;
using System.Text;

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
    private CancellationTokenSource? _runtimeSetup;
    private CancellationTokenSource? _gameSetup;
    private TouchControlOverlay? _touchOverlay;

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

        if (!AndroidRuntimeStore.IsInstalled(this))
            AddRuntimeSetup(root);
        else
            AddGameSetup(root);

        SetContentView(root);
        EnterImmersiveMode();
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
        _runtimeSetup?.Cancel();
        _runtimeSetup?.Dispose();
        _runtimeSetup = null;
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

    private void AddRuntimeSetup(FrameLayout root)
    {
        var (backdrop, content, status) = AddSetupShell(root, L.T("mobile.runtime.missing"));
        var progress = CreateSetupProgress();
        content.AddView(progress, SetupRow(Dp(6), Dp(18), Dp(4)));
        var install = CreateSetupButton(L.T("mobile.runtime.install"), true);
        var cancel = CreateSetupButton(L.T("mobile.runtime.cancel"), false);
        cancel.Visibility = ViewStates.Gone;
        var buttons = CreateButtonRow(install, cancel);
        content.AddView(buttons, SetupRow(ViewGroup.LayoutParams.WrapContent, Dp(20), 0));

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
                await FadeOutAsync(backdrop, content);
                root.RemoveView(backdrop);
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
        var (backdrop, content, status) = AddSetupShell(root, L.T("mobile.game.setup"));
        var playerName = new EditText(this)
        {
            Hint = L.T("mobile.game.player_name"),
            Text = preferences.GetString("offline-player", "Player")
        };
        playerName.SetTextColor(Color.White);
        playerName.SetHintTextColor(Color.LightGray);
        playerName.SetSingleLine(true);
        playerName.TextSize = 16;
        playerName.SetPadding(Dp(16), 0, Dp(16), 0);
        playerName.Background = Rounded(Color.Argb(238, 37, 41, 47), Dp(8), Color.Argb(170, 94, 102, 112));
        content.AddView(playerName, SetupRow(Dp(54), Dp(18), 0));
        var progress = CreateSetupProgress();
        content.AddView(progress, SetupRow(Dp(6), Dp(16), Dp(4)));
        var play = CreateSetupButton(L.F("mobile.game.install_play", AndroidEmbeddedGame.FirstPlayableVersion), true);
        var cancel = CreateSetupButton(L.T("mobile.runtime.cancel"), false);
        cancel.Visibility = ViewStates.Gone;
        var buttons = CreateButtonRow(play, cancel);
        content.AddView(buttons, SetupRow(ViewGroup.LayoutParams.WrapContent, Dp(20), 0));

        play.Click += async (_, _) =>
        {
            _gameSetup?.Dispose();
            _gameSetup = new CancellationTokenSource();
            play.Enabled = false;
            playerName.Enabled = false;
            playerName.Visibility = ViewStates.Gone;
            play.Visibility = ViewStates.Gone;
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
                progress.Progress = 100;
                status.Text = L.T("mobile.game.preparing");
                var runTask = Task.Run(() => AndroidEmbeddedGame.Run(prepared), _gameSetup.Token);
                await Task.Delay(900);
                await RevealGameAsync(backdrop, content);
                await runTask;
                _touchOverlay?.Animate().Cancel();
                if (_touchOverlay is not null)
                {
                    _touchOverlay.Visibility = ViewStates.Gone;
                    _touchOverlay.Alpha = 0;
                }
                status.Text = L.T("mobile.game.exited");
                backdrop.Visibility = ViewStates.Visible;
                content.Visibility = ViewStates.Visible;
                backdrop.Alpha = 0;
                content.Alpha = 0;
                backdrop.Animate().Alpha(1).SetDuration(240).Start();
                content.Animate().Alpha(1).SetDuration(240).Start();
            }
            catch (System.OperationCanceledException)
            {
                status.Text = L.T("mobile.game.setup");
                RestoreSetup(backdrop, content);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException
                                               or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                status.Text = L.F("mobile.game.failed", exception.Message);
                RestoreSetup(backdrop, content);
            }
            finally
            {
                play.Enabled = true;
                play.Visibility = ViewStates.Visible;
                playerName.Enabled = true;
                playerName.Visibility = ViewStates.Visible;
                cancel.Visibility = ViewStates.Gone;
                progress.Visibility = ViewStates.Gone;
            }
        };
        cancel.Click += (_, _) => _gameSetup?.Cancel();
    }

    private (View Backdrop, LinearLayout Content, TextView Status) AddSetupShell(
        FrameLayout root, string initialStatus)
    {
        var backdrop = new GameLoadingBackdrop(this);
        root.AddView(backdrop, MatchParent());

        var content = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        content.SetGravity(GravityFlags.CenterHorizontal);
        content.Background = Rounded(Color.Argb(238, 24, 27, 31), Dp(14),
            Color.Argb(175, 105, 113, 122));
        content.Elevation = Dp(12);
        content.SetPadding(Dp(28), Dp(24), Dp(28), Dp(24));

        var mark = new TextView(this) { Text = "J", Gravity = GravityFlags.Center };
        mark.SetTextColor(Color.Rgb(31, 24, 13));
        mark.TextSize = 26;
        mark.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        mark.Background = Rounded(Color.Rgb(217, 138, 30), Dp(8));
        content.AddView(mark, new LinearLayout.LayoutParams(Dp(52), Dp(52)));

        var brand = new TextView(this) { Text = "JAY", Gravity = GravityFlags.Center };
        brand.SetTextColor(Color.White);
        brand.TextSize = 24;
        brand.LetterSpacing = 0.12f;
        brand.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        content.AddView(brand, SetupRow(ViewGroup.LayoutParams.WrapContent, Dp(10), 0));

        var status = new TextView(this) { Text = initialStatus, Gravity = GravityFlags.Center };
        status.SetTextColor(Color.Rgb(218, 222, 227));
        status.TextSize = 15;
        status.SetLineSpacing(0, 1.12f);
        content.AddView(status, SetupRow(ViewGroup.LayoutParams.WrapContent, Dp(8), 0));

        var metrics = Resources?.DisplayMetrics;
        var width = Math.Min((metrics?.WidthPixels ?? Dp(600)) - Dp(48), Dp(560));
        var panel = new FrameLayout.LayoutParams(width, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center);
        root.AddView(content, panel);
        return (backdrop, content, status);
    }

    private ProgressBar CreateSetupProgress()
    {
        var progress = new ProgressBar(this, null, 0, Resource.Style.JayProgressBarHorizontal)
        {
            Max = 100,
            Visibility = ViewStates.Gone,
            Progress = 0
        };
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
        {
            progress.ProgressTintList = global::Android.Content.Res.ColorStateList.ValueOf(Color.Rgb(217, 138, 30));
            progress.ProgressBackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(Color.Rgb(55, 60, 67));
        }
        return progress;
    }

    private Button CreateSetupButton(string text, bool primary)
    {
        var button = new Button(this)
        {
            Text = text,
            StateListAnimator = null
        };
        button.SetAllCaps(false);
        button.SetMinHeight(Dp(50));
        button.SetMinWidth(Dp(148));
        button.SetPadding(Dp(18), 0, Dp(18), 0);
        button.SetTextColor(primary ? Color.Rgb(31, 24, 13) : Color.White);
        button.TextSize = 15;
        button.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        button.Background = Rounded(primary ? Color.Rgb(217, 138, 30) : Color.Rgb(48, 53, 60),
            Dp(8), primary ? Color.Rgb(245, 184, 83) : Color.Rgb(105, 113, 122));
        return button;
    }

    private LinearLayout CreateButtonRow(params Button[] buttons)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.Center);
        foreach (var button in buttons)
        {
            var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(50));
            parameters.SetMargins(Dp(5), 0, Dp(5), 0);
            row.AddView(button, parameters);
        }
        return row;
    }

    private LinearLayout.LayoutParams SetupRow(int height, int topMargin, int bottomMargin)
    {
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, height);
        parameters.SetMargins(0, topMargin, 0, bottomMargin);
        return parameters;
    }

    private async Task RevealGameAsync(View backdrop, View content)
    {
        if (_touchOverlay is not null)
        {
            _touchOverlay.Visibility = ViewStates.Visible;
            _touchOverlay.Alpha = 0;
            _touchOverlay.Animate().Alpha(1).SetDuration(320).Start();
            _touchOverlay.UpdateHardwareCapture(HasWindowFocus);
        }
        await FadeOutAsync(backdrop, content);
        backdrop.Visibility = ViewStates.Gone;
        content.Visibility = ViewStates.Gone;
    }

    private void RestoreSetup(View backdrop, View content)
    {
        _touchOverlay?.Animate().Cancel();
        if (_touchOverlay is not null)
        {
            _touchOverlay.UpdateHardwareCapture(false);
            _touchOverlay.Visibility = ViewStates.Gone;
            _touchOverlay.Alpha = 0;
        }

        backdrop.Animate().Cancel();
        content.Animate().Cancel();
        backdrop.Visibility = ViewStates.Visible;
        content.Visibility = ViewStates.Visible;
        backdrop.Alpha = 1;
        content.Alpha = 1;
    }

    private static async Task FadeOutAsync(params View[] views)
    {
        foreach (var view in views) view.Animate().Alpha(0).SetDuration(260).Start();
        await Task.Delay(280);
    }

    private static GradientDrawable Rounded(Color fill, int radius, Color? stroke = null)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(radius);
        if (stroke is { } border) drawable.SetStroke(1, border);
        return drawable;
    }

    private int Dp(float value) => (int)Math.Round(value * (Resources?.DisplayMetrics?.Density ?? 1));

    public void SurfaceCreated(ISurfaceHolder holder) => NativeJvmHost.SetSurface(holder.Surface);

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) =>
        NativeJvmHost.SetSurface(holder.Surface);

    public void SurfaceDestroyed(ISurfaceHolder holder) => NativeJvmHost.SetSurface(null);

    private static FrameLayout.LayoutParams MatchParent() => new(
        ViewGroup.LayoutParams.MatchParent,
        ViewGroup.LayoutParams.MatchParent);
}

/// <summary>A lightweight, original loading scene that avoids showing a blank surface or stock Android chrome.</summary>
internal sealed class GameLoadingBackdrop(Context context) : View(context)
{
    private readonly Paint _paint = new() { AntiAlias = false };
    private readonly Paint _glow = new() { AntiAlias = true };
    private long _startedAt = SystemClock.UptimeMillis();

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var width = Width;
        var height = Height;
        if (width <= 0 || height <= 0) return;

        _paint.SetShader(new LinearGradient(0, 0, width, height,
            Color.Rgb(19, 23, 29), Color.Rgb(38, 31, 24), Shader.TileMode.Clamp));
        canvas.DrawRect(0, 0, width, height, _paint);
        _paint.SetShader(null);

        var cell = Math.Max(22, Math.Min(width, height) / 18);
        var elapsed = (SystemClock.UptimeMillis() - _startedAt) / 1000f;
        for (var row = 0; row <= height / cell + 1; row++)
        for (var col = 0; col <= width / cell + 1; col++)
        {
            var wave = Math.Sin(col * 0.72 + row * 0.51 + elapsed * 0.35);
            if (wave < 0.60) continue;
            var alpha = (int)(8 + (wave - 0.60) * 22);
            _paint.Color = Color.Argb(alpha, 217, 138, 30);
            var left = col * cell;
            var top = row * cell;
            canvas.DrawRect(left + 1, top + 1, left + cell - 2, top + cell - 2, _paint);
        }

        _glow.SetShader(new RadialGradient(width * 0.5f, height * 0.48f,
            Math.Min(width, height) * 0.58f,
            Color.Argb(55, 217, 138, 30), Color.Transparent, Shader.TileMode.Clamp));
        canvas.DrawCircle(width * 0.5f, height * 0.48f, Math.Min(width, height) * 0.58f, _glow);
        _glow.SetShader(null);
        PostInvalidateDelayed(66);
    }
}

internal sealed class TouchControlOverlay : View, ITouchInputSink
{
    private const long LongPressDelayMs = 340;
    private const long LongPressRingMs = 900;
    private readonly Paint _fill = new() { AntiAlias = true, Color = Color.Argb(142, 48, 53, 59) };
    private readonly Paint _activeFill = new() { AntiAlias = true, Color = Color.Argb(218, 105, 112, 120) };
    private readonly Paint _buttonFace = new() { AntiAlias = false, Color = Color.Argb(82, 76, 82, 86) };
    private readonly Paint _buttonPressed = new() { AntiAlias = false, Color = Color.Argb(145, 142, 146, 150) };
    private readonly Paint _buttonShadow = new() { AntiAlias = false, Color = Color.Argb(155, 8, 10, 12) };
    private readonly Paint _buttonHighlight = new() { AntiAlias = false, Color = Color.Argb(150, 238, 241, 244), StrokeWidth = 2 };
    private readonly Paint _buttonBorder = new() { AntiAlias = false, Color = Color.Argb(145, 225, 228, 232), StrokeWidth = 2 };
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
    private bool _settingsOpen;
    private bool _layoutEditing;
    private int? _editingPointer;
    private TouchControlAction? _editingAction;
    private float _controlOpacity = 0.62f;
    private float _lastMouseX;
    private float _lastMouseY;
    private bool _hasMousePosition;

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
        // This view is composited over a SurfaceView. Clear its transparent layer on every frame;
        // otherwise some Android GPU drivers retain the previous joystick knob position.
        canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear);
        var gameplayControlsActive = NativeJvmHost.GameplayControlsActive;
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
        DrawSettingRow(canvas, "摇杆 + 点击互动", 0.20f, _profile.Mode == TouchControlMode.JoystickAndTap);
        DrawSettingRow(canvas, "方向键 + 点击互动", 0.29f, _profile.Mode == TouchControlMode.DPadAndTap);
        DrawSettingRow(canvas, "摇杆 + 准星", 0.38f, _profile.Mode == TouchControlMode.JoystickAndCrosshair);
        DrawSettingRow(canvas, "外接键盘鼠标", 0.47f, _profile.Mode == TouchControlMode.ExternalKeyboardMouse);
        DrawStepper(canvas, $"按键大小  {Math.Round(_profile.Scale * 100):0}%", 0.58f);
        DrawStepper(canvas, $"透明度  {Math.Round(_controlOpacity * 100):0}%", 0.67f);
        DrawSettingRow(canvas, _profile.ForcedSprint ? "强制疾跑：开" : "强制疾跑：关", 0.76f,
            _profile.ForcedSprint);
        DrawPanelButton(canvas, "恢复默认", 0.615f, 0.83f, 0.77f, 0.90f);
        DrawPanelButton(canvas, "编辑布局", 0.80f, 0.83f, 0.955f, 0.90f);
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
        _buttonFace.Color = WithAlpha(Color.Argb(82, 76, 82, 86), _controlOpacity);
        _buttonPressed.Color = WithAlpha(Color.Argb(145, 142, 146, 150), _controlOpacity);
        _buttonShadow.Color = WithAlpha(Color.Argb(155, 8, 10, 12), Math.Max(0.55f, _controlOpacity));
        _buttonHighlight.Color = WithAlpha(Color.Argb(150, 238, 241, 244), Math.Max(0.45f, _controlOpacity));
        _buttonBorder.Color = WithAlpha(Color.Argb(145, 225, 228, 232), Math.Max(0.45f, _controlOpacity));
        _joystickMark.Color = WithAlpha(Color.Argb(145, 235, 238, 242), Math.Max(0.45f, _controlOpacity));
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
        var shadow = new RectF(bounds);
        shadow.Offset(Math.Max(2, radius * 0.08f), Math.Max(3, radius * 0.12f));
        canvas.DrawRect(shadow, _buttonShadow);
        _buttonBorder.SetStyle(Paint.Style.Stroke);
        canvas.DrawRect(bounds, pressed ? _buttonPressed : _buttonFace);
        canvas.DrawRect(bounds, _buttonBorder);
        _buttonHighlight.StrokeWidth = Math.Max(1, radius * 0.045f);
        canvas.DrawLine(bounds.Left + 2, bounds.Top + 2, bounds.Right - 2, bounds.Top + 2, _buttonHighlight);
        canvas.DrawLine(bounds.Left + 2, bounds.Top + 2, bounds.Left + 2, bounds.Bottom - 2, _buttonHighlight);
        DrawGlyph(canvas, control.Action, x, y + pressOffset,
            Math.Min(halfWidth, halfHeight) * 0.68f, label);
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

    private static bool TryGetHotbarSlot(double x, double y, out int slot)
    {
        const double left = 0.405;
        const double right = 0.595;
        if (y < 0.905 || x < left || x >= right)
        {
            slot = -1;
            return false;
        }
        slot = Math.Clamp((int)((x - left) / ((right - left) / 9)), 0, 8);
        return true;
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
        var previousMode = _profile.Mode;
        if (y is > 0.16f and < 0.24f) _profile.Mode = TouchControlMode.JoystickAndTap;
        else if (y is > 0.25f and < 0.33f) _profile.Mode = TouchControlMode.DPadAndTap;
        else if (y is > 0.34f and < 0.42f) _profile.Mode = TouchControlMode.JoystickAndCrosshair;
        else if (y is > 0.43f and < 0.51f) _profile.Mode = TouchControlMode.ExternalKeyboardMouse;
        else if (y is > 0.54f and < 0.62f && x < 0.70f) _profile.Scale = Math.Max(0.70, _profile.Scale - 0.05);
        else if (y is > 0.54f and < 0.62f && x > 0.87f) _profile.Scale = Math.Min(1.35, _profile.Scale + 0.05);
        else if (y is > 0.63f and < 0.71f && x < 0.70f) _controlOpacity = Math.Max(0.30f, _controlOpacity - 0.05f);
        else if (y is > 0.63f and < 0.71f && x > 0.87f) _controlOpacity = Math.Min(1f, _controlOpacity + 0.05f);
        else if (y is > 0.72f and < 0.80f) _profile.ForcedSprint = !_profile.ForcedSprint;
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
                var gameplay = NativeJvmHost.GameplayControlsActive;
                // The inventory button intentionally sits flush against the hotbar. Its circular
                // touch target slightly overlaps the ninth-slot rectangle on some aspect ratios,
                // so visible overlay controls must win before we fall back to hotbar selection.
                var control = _profile.HitTest(x, y, gameplay);
                if (gameplay && control is null && TryGetHotbarSlot(x, y, out var slot))
                {
                    _hotbarPointers.Add(pointerId);
                    NativeJvmHost.AddKey(49 + slot);
                    return true;
                }
                var handled = _router.Press(pointerId, x, y, gameplay);
                if (handled && gameplay && control is null)
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

    public override bool OnGenericMotionEvent(MotionEvent? e) =>
        HandleHardwareMotion(e) || base.OnGenericMotionEvent(e);

    public override bool OnHoverEvent(MotionEvent? e) =>
        HandleHardwareMotion(e) || base.OnHoverEvent(e);

    public override bool OnCapturedPointerEvent(MotionEvent? e) =>
        HandleHardwareMotion(e) || base.OnCapturedPointerEvent(e);

    public override bool OnKeyPreIme(Keycode keyCode, KeyEvent? e) =>
        HandleHardwareKey(e) || base.OnKeyPreIme(keyCode, e);

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e) =>
        HandleHardwareKey(e) || base.OnKeyDown(keyCode, e);

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e) =>
        HandleHardwareKey(e) || base.OnKeyUp(keyCode, e);

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

    public bool HandleHardwareKey(KeyEvent? e)
    {
        // Physical keys stay available in every touch layout. The dedicated external-input
        // profile controls visibility of touch buttons; it must not gate keyboard events.
        if (e is null || Visibility != ViewStates.Visible) return false;
        var pressed = e.Action == KeyEventActions.Down;
        if (e.Action is not (KeyEventActions.Down or KeyEventActions.Up)) return true;
        if (pressed) _hardwareKeys.Add(e.KeyCode); else _hardwareKeys.Remove(e.KeyCode);

        if (IsMovementKey(e.KeyCode))
        {
            UpdateHardwareMovement();
            return true;
        }

        if (HardwareAction(e.KeyCode) is { } action)
        {
            AndroidGameInputBridge.SetAction(action, pressed);
            return true;
        }

        if (!pressed || e.RepeatCount > 0) return true;
        if (GlfwKey(e.KeyCode) is { } glfw) NativeJvmHost.AddKey(glfw);
        if (!NativeJvmHost.GameplayControlsActive && e.UnicodeChar > 0)
            NativeJvmHost.AddCodePoint(e.UnicodeChar);
        return true;
    }

    public bool HandleHardwareMotion(MotionEvent? e)
    {
        if (e is null || Visibility != ViewStates.Visible || !IsHardwarePointer(e)) return false;

        if (e.ActionMasked == MotionEventActions.Scroll)
        {
            var scroll = e.GetAxisValue(Axis.Vscroll);
            if (Math.Abs(scroll) > 0.01f)
            {
                var action = scroll > 0 ? TouchControlAction.PreviousSlot : TouchControlAction.NextSlot;
                AndroidGameInputBridge.SetAction(action, true);
                AndroidGameInputBridge.SetAction(action, false);
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
            if (!NativeJvmHost.GameplayControlsActive && Width > 0 && Height > 0)
                AndroidGameInputBridge.SetPointer(e.GetX() / Width, e.GetY() / Height, false);
            if (action is { } mapped) AndroidGameInputBridge.SetAction(mapped, pressed);
            return true;
        }

        if (e.ActionMasked is not (MotionEventActions.HoverMove or MotionEventActions.Move)) return true;
        if (NativeJvmHost.GameplayControlsActive)
        {
            var dx = e.GetAxisValue(Axis.RelativeX);
            var dy = e.GetAxisValue(Axis.RelativeY);
            if (Math.Abs(dx) < 0.001f && Math.Abs(dy) < 0.001f && _hasMousePosition)
            {
                dx = e.GetX() - _lastMouseX;
                dy = e.GetY() - _lastMouseY;
            }
            if (Width > 0 && Height > 0 && (Math.Abs(dx) > 0.001f || Math.Abs(dy) > 0.001f))
                AndroidGameInputBridge.AddLook(dx / Width, dy / Height);
        }
        else if (Width > 0 && Height > 0)
        {
            AndroidGameInputBridge.SetPointer(e.GetX() / Width, e.GetY() / Height, false);
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
            _profile.Mode == TouchControlMode.ExternalKeyboardMouse)
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
        if (_hardwareKeys.Count == 0 && _profile.Mode != TouchControlMode.ExternalKeyboardMouse) return;
        _hardwareKeys.Clear();
        AndroidGameInputBridge.SetMovement(0, 0);
        foreach (var action in new[]
                 {
                     TouchControlAction.Jump, TouchControlAction.Sneak, TouchControlAction.Sprint,
                     TouchControlAction.Inventory, TouchControlAction.Menu, TouchControlAction.Keyboard,
                     TouchControlAction.Drop, TouchControlAction.PrimaryAction, TouchControlAction.SecondaryAction
                 })
            AndroidGameInputBridge.SetAction(action, false);
        _hasMousePosition = false;
    }

    private static bool IsMovementKey(Keycode key) => key is Keycode.W or Keycode.A or Keycode.S or Keycode.D
        or Keycode.DpadUp or Keycode.DpadLeft or Keycode.DpadDown or Keycode.DpadRight;

    private void UpdateHardwareMovement()
    {
        var left = _hardwareKeys.Contains(Keycode.A) || _hardwareKeys.Contains(Keycode.DpadLeft);
        var right = _hardwareKeys.Contains(Keycode.D) || _hardwareKeys.Contains(Keycode.DpadRight);
        var forward = _hardwareKeys.Contains(Keycode.W) || _hardwareKeys.Contains(Keycode.DpadUp);
        var back = _hardwareKeys.Contains(Keycode.S) || _hardwareKeys.Contains(Keycode.DpadDown);
        AndroidGameInputBridge.SetMovement((right ? 1 : 0) - (left ? 1 : 0),
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
