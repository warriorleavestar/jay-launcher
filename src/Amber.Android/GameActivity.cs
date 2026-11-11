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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);

        var root = new FrameLayout(this);
        var surface = new SurfaceView(this);
        surface.Holder!.AddCallback(this);
        root.AddView(surface, MatchParent());
        root.AddView(new TouchControlOverlay(this), MatchParent());

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
                status.Text = L.T("mobile.game.exited");
                root.AddView(content, panel);
            }
            catch (System.OperationCanceledException)
            {
                status.Text = L.T("mobile.game.setup");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException
                                               or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
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
}

internal sealed class TouchControlOverlay : View, ITouchInputSink
{
    private readonly Paint _fill = new() { AntiAlias = true, Color = Color.Argb(110, 25, 29, 36) };
    private readonly Paint _stroke = new() { AntiAlias = true, Color = Color.Argb(210, 238, 174, 78), StrokeWidth = 3, StrokeCap = Paint.Cap.Round };
    private readonly Paint _text = new() { AntiAlias = true, Color = Color.White, TextAlign = Paint.Align.Center, TextSize = 24 };
    private readonly TouchInputRouter _router;

    public TouchControlOverlay(Context context) : base(context)
    {
        _router = new TouchInputRouter(this);
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
        _stroke.SetStyle(Paint.Style.Stroke);
        foreach (var control in TouchControlLayout.Default)
        {
            if (!gameplayControlsActive && control.Action != TouchControlAction.Menu) continue;
            var x = (float)(control.CenterX * Width);
            var y = (float)(control.CenterY * Height);
            var radius = (float)(control.Radius * Math.Min(Width, Height));
            canvas.DrawCircle(x, y, radius, _fill);
            canvas.DrawCircle(x, y, radius, _stroke);
            if (control.Label.Length > 0)
                canvas.DrawText(control.Label, x, y - (_text.Ascent() + _text.Descent()) / 2, _text);
        }
        PostInvalidateDelayed(100);
    }

    protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
    {
        base.OnSizeChanged(width, height, oldWidth, oldHeight);
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q) return;
#pragma warning disable CA1416
        SystemGestureExclusionRects = [new Rect(0, Math.Max(0, height - 200), width, height)];
#pragma warning restore CA1416
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        var actionIndex = e.ActionIndex;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                return _router.Press(e.GetPointerId(actionIndex), e.GetX(actionIndex) / Width,
                    e.GetY(actionIndex) / Height, NativeJvmHost.GameplayControlsActive);
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

    public void SetAction(TouchControlAction action, bool pressed)
    {
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
        input?.ShowSoftInput(this, ShowFlags.Implicit);
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
}
