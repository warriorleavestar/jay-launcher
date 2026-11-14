using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Amber.App.Services;

/// <summary>
/// The one place pseudo-3D motion goes through (cards that lean toward the pointer, a page that comes forward, the scene
/// behind the pages, the turning skin figure). <see cref="Current"/> is what applies now: the theme's "motion3d" numbers
/// (or Jay's), or <see cref="Motion3D.Flat"/> when reduced motion is on or the "3D effects" setting is off. Safety
/// surfaces never move, whatever this says.
/// </summary>
public static class Depth3D
{
    private static bool _enabled = true;
    private static bool _started;

    public static Motion3D Current { get; private set; } = Motion3D.Default;

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Self-checks that measure positions right after changing page set this: a page still coming forward is a little
    /// smaller for a moment. The motion3d scope leaves it off to check the effect itself.
    /// </summary>
    internal static bool PageEnterSuppressed { get; set; }

    /// <summary>Times the page-enter effect ran (the self-check counts them).</summary>
    internal static int PageEnters { get; private set; }

    public static void Start(bool enabled)
    {
        _enabled = enabled;
        if (!_started)
        {
            _started = true;
            ThemeService.Changed += Recompute;
            SystemMotion.Changed += Recompute;
        }
        Recompute();
    }

    /// <summary>The "3D effects" setting (Settings › Appearance) changed.</summary>
    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Recompute();
    }

    public static bool Enabled => _enabled;

    private static void Recompute()
    {
        var next = Motion3D.Effective(ThemeService.Current?.Motion3D, _enabled, SystemMotion.Reduced);
        if (next == Current) return;
        Current = next;
        Changed?.Invoke();
    }

    /// <summary>The scene pictures of the current theme, far to near, or empty.</summary>
    public static IReadOnlyList<(Bitmap Bitmap, double Depth)> SceneLayers { get; private set; } = [];
    public static ThemeScene? Scene { get; private set; }

    /// <summary>Called by <see cref="ThemeService"/> when a pack is applied: decodes the scene layers (a broken picture is left out and noted on the pack).</summary>
    internal static IReadOnlyList<(Bitmap, double)> LoadScene(ThemePack? pack)
    {
        var old = SceneLayers;
        var layers = new List<(Bitmap, double)>();
        if (pack is { Scene: { } scene, Directory: { } dir })
        {
            foreach (var layer in scene.Layers)
            {
                if (ThemeImages.Load(Path.Combine(dir, layer.Image), ThemeSlots.Scene, new ThemeSlot { Image = layer.Image }) is { } bitmap) layers.Add((bitmap, layer.Depth));
                else pack.Problems.Add(new("bad_image", layer.Image));
            }
        }
        Scene = layers.Count > 0 ? pack!.Scene : null;
        SceneLayers = layers;
        return old;
    }

    /// <summary>
    /// A new page comes forward from a little behind the screen while it fades in (theme "pageDepth"). Nothing happens
    /// when 3D effects are off or motion is reduced.
    /// </summary>
    public static void EnterPage(Control host)
    {
        if (Current.PageDepth <= 0 || PageEnterSuppressed) return;
        var duration = Application.Current?.TryGetResource("MotionSlow", null, out var d) == true && d is TimeSpan t ? t : TimeSpan.FromMilliseconds(240);
        EnterPage(host, Current.PageDepth, duration);
    }

    /// <summary>The longest a page takes to come forward, whatever a theme sets for its slow motion.</summary>
    internal static readonly TimeSpan MaxPageEnter = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// <see cref="EnterPage(Control)"/> with its numbers. Locked safety surfaces are never scaled or faded: a page that
    /// has one comes in flat, and a run stops (the page at full size and fully opaque) as soon as one shows up in it.
    /// </summary>
    internal static void EnterPage(Control host, double depth, TimeSpan duration)
    {
        depth = Math.Clamp(depth, 0, Motion3D.MaxPageDepth);
        if (depth <= 0 || HasSafetySurface(host)) return;
        if (duration > MaxPageEnter) duration = MaxPageEnter;
        PageEnters++;
        var scale = new ScaleTransform(1 - depth, 1 - depth);
        host.RenderTransformOrigin = RelativePoint.Center;
        host.RenderTransform = scale;
        host.Opacity = 0;
        var flat = false;
        void Flatten()
        {
            host.RenderTransform = null;
            host.Opacity = 1;
        }
        Animate(host, duration, k =>
        {
            if (flat) return;
            if (HasSafetySurface(host))
            {
                flat = true;
                Flatten();
                return;
            }
            var s = 1 - depth * (1 - k);
            scale.ScaleX = scale.ScaleY = s;
            host.Opacity = Math.Min(1, k * 1.6);
        }, Flatten);
    }

    private static bool HasSafetySurface(Control host) =>
        host is Controls.Safety.SafetySurface or Controls.Safety.SafetyButton or Controls.Safety.PrivacyRow
        || Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(host).Any(v => v is Controls.Safety.SafetySurface or Controls.Safety.SafetyButton or Controls.Safety.PrivacyRow);

    private static readonly Dictionary<Control, (DispatcherTimer Timer, Action Tick)> Running = [];

    /// <summary>
    /// Runs <paramref name="step"/> from 0 to 1 with an ease-out over <paramref name="duration"/>, about 60 times a second.
    /// A new run on the same control replaces the old one.
    /// </summary>
    internal static void Animate(Control control, TimeSpan duration, Action<double> step, Action? done = null)
    {
        if (Running.Remove(control, out var old)) old.Timer.Stop();
        if (duration <= TimeSpan.Zero)
        {
            step(1);
            done?.Invoke();
            return;
        }
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        void Tick()
        {
            var t = Math.Min(1, (DateTime.UtcNow - start) / duration);
            step(1 - Math.Pow(1 - t, 3));
            if (t < 1) return;
            timer.Stop();
            Running.Remove(control);
            done?.Invoke();
        }
        timer.Tick += (_, _) => Tick();
        Running[control] = (timer, Tick);
        step(0);
        timer.Start();
    }

    /// <summary>Tests: one frame of the run on this control now, if there is one.</summary>
    internal static void TickForTest(Control control)
    {
        if (Running.TryGetValue(control, out var run)) run.Tick();
    }

    /// <summary>Stops a run started by <see cref="Animate"/> without finishing it.</summary>
    internal static void Stop(Control control)
    {
        if (Running.Remove(control, out var old)) old.Timer.Stop();
    }
}
