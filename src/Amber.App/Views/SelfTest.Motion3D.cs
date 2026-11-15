using Amber.App.Controls.Safety;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Navigation;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using SkiaSharp;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=motion3d: pseudo-3D motion. A library card leans toward the pointer and settles flat; nothing
/// inside a safety surface leans; a new page comes forward; reduced motion and the "3D effects" setting turn all of it
/// off; a theme pack's "motion3d" numbers and layered scene (pictures drawn in the report folder) apply, the near layer
/// moving most; the "Me" page's skin figure (drawn from the name for an offline account) turns with ← → and Home, turns
/// by itself under the pointer and settles back, and doesn't turn by itself with reduced motion. No network.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task Motion3DAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var originalMode = service.Settings.ReduceMotionMode;
        var game = Path.Combine(reportDir, "motion-game");
        var settings = Page<SettingsViewModel>();
        var library = main.Pages.OfType<LibraryViewModel>().First();
        try
        {
            Check("自检使用显式独立数据目录", Environment.GetEnvironmentVariable("AMBER_DATA_DIR") is { Length: > 0 });
            SystemMotion.Apply(Amber.Core.Layout.MotionPreference.Off);
            settings.Effects3D = true;
            WriteLibraryVersion(game, "fabric-1.20.1", """[{"name":"net.fabricmc:fabric-loader:0.16.9"}]""");
            WriteLibraryVersion(game, "1.21.4", "[]");
            WriteLibraryVersion(game, "neoforge-1.21.1", """[{"name":"net.neoforged:neoforge:21.1.1"}]""");
            service.Settings.Instances = new() { ["fabric-1.20.1"] = new() { DisplayName = "生存整合包" } };
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            await Task.Delay(1500);
            window.Width = 1280;
            window.Height = 800;
            main.Shell.Navigate("library");
            await WaitAsync(() => library.IsLoaded && library.TotalCount == 3, 10);
            await Task.Delay(600);

            // 1. Defaults: cards lean, pages come forward.
            Check("默认：立体效果打开，卡片倾斜 8°、新页面从后面浮上来", Depth3D.Current == Motion3D.Default && !Depth3D.Current.IsFlat, $"{Depth3D.Current}");
            var card = window.PageHost.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("instance-card") && b.IsEffectivelyVisible);
            Check("游戏库的封面卡片带着倾斜效果", card is not null && TiltEffect.GetIsOn(card));
            if (card is not null)
            {
                var leaned = TiltEffect.LeanTo(card, new Point(card.Bounds.Width, 0));
                var (ax, ay) = TiltEffect.Angles(card);
                Check("指针在右上角：卡片右上角往后倒（绕横轴、竖轴各 8°）", leaned && Math.Abs(ax - Motion3D.DefaultTilt) < 0.01 && Math.Abs(ay - Motion3D.DefaultTilt) < 0.01 && card.RenderTransform is Rotate3DTransform,
                    $"{ax:0.##}, {ay:0.##}");
                await ShotAsync("motion-card-lean");
                TiltEffect.LeanTo(card, new Point(card.Bounds.Width / 2, card.Bounds.Height / 2));
                Check("指针在正中：卡片是平的", TiltEffect.Angles(card) == (0, 0));
                TiltEffect.LeanTo(card, new Point(0, card.Bounds.Height));
                TiltEffect.Release(card);
                await WaitAsync(() => card.RenderTransform is null, 2);
                Check("指针离开：卡片慢慢回平", card.RenderTransform is null && TiltEffect.Angles(card) == (0, 0));
            }

            // 2. Safety surfaces never move.
            var row = window.GetVisualDescendants().OfType<PrivacyRow>().FirstOrDefault(r => r.IsEffectivelyVisible);
            if (row is not null)
            {
                TiltEffect.SetIsOn(row, true);
                var moved = TiltEffect.LeanTo(row, new Point(row.Bounds.Width, 0));
                Check("安全外观（隐私行）就算标了倾斜也不动", !moved && row.RenderTransform is null && TiltEffect.IsLocked(row));
                TiltEffect.SetIsOn(row, false);
            }
            else Check("找得到隐私行", false);

            // 3. A new page comes forward.
            var enters = Depth3D.PageEnters;
            main.Shell.Navigate("discover");
            await WaitAsync(() => Depth3D.PageEnters > enters, 2);
            var midway = window.PageHost.RenderTransform is ScaleTransform s && s.ScaleX < 1;
            Check("换页：新页面从稍远处浮上来（缩小一点、淡入）", Depth3D.PageEnters == enters + 1 && midway);
            await WaitAsync(() => window.PageHost.RenderTransform is null, 2);
            Check("换页：动完回到原大小、完全不透明", window.PageHost.RenderTransform is null && window.PageHost.Opacity == 1);
            main.Shell.Navigate("library");
            await Task.Delay(600);

            // 4. Reduced motion: nothing moves.
            SystemMotion.Apply(Amber.Core.Layout.MotionPreference.On);
            Check("减少动画：立体效果全关", Depth3D.Current.IsFlat);
            card = window.PageHost.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("instance-card") && b.IsEffectivelyVisible);
            Check("减少动画：卡片不倾斜", card is not null && !TiltEffect.LeanTo(card, new Point(card.Bounds.Width, 0)) && card.RenderTransform is null);
            enters = Depth3D.PageEnters;
            main.Shell.Navigate("discover");
            await Task.Delay(500);
            Check("减少动画：换页没有浮上来的效果", Depth3D.PageEnters == enters && window.PageHost.RenderTransform is null);
            SystemMotion.Apply(Amber.Core.Layout.MotionPreference.Off);
            Check("关掉减少动画：立体效果回来", !Depth3D.Current.IsFlat);

            // 5. The setting.
            main.Shell.Navigate("settings#Motion3D");
            await Task.Delay(800);
            var box = window.PageHost.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Name == "Motion3DBox");
            Check("settings#Motion3D 打开外观分组，看得到“立体效果”", settings.SelectedGroupId == SettingsCatalog.Appearance && box is { IsEffectivelyVisible: true, IsChecked: true });
            await ShotAsync("motion-setting");
            settings.Effects3D = false;
            Check("关掉“立体效果”：全关，并存进设置", Depth3D.Current.IsFlat && !service.Settings.Motion3D);
            settings.Effects3D = true;
            Check("打开“立体效果”：回来，并存进设置", !Depth3D.Current.IsFlat && service.Settings.Motion3D);

            // 6. A theme pack's numbers and its scene.
            var pack = WriteScenePack();
            Check("带场景的主题包读得出来、没有问题", pack.Problems.Count == 0 && pack.Scene?.Layers.Count == 3 && pack.Motion3D?.Tilt == 6,
                string.Join("; ", pack.Problems.Select(p => p.ToString())));
            ThemeService.Apply(pack);
            main.Shell.Navigate("library");
            await Task.Delay(800);
            var scene = window.GetVisualDescendants().OfType<SceneView>().FirstOrDefault();
            Check("主题的 motion3d 生效（倾斜 6°、视差 20）", Depth3D.Current.Tilt == 6 && Depth3D.Current.Parallax == 20);
            Check("场景画在页面后面，三层", scene is { IsVisible: true, HasScene: true, IsHitTestVisible: false } && Depth3D.SceneLayers.Count == 3);
            if (scene is not null)
            {
                scene.PointTo(new Point(scene.Bounds.Width, scene.Bounds.Height / 2));
                await Task.Delay(300);
                var o = scene.Offsets;
                Check("指针在右边：远景不动，中景动一点，近景动 20 像素且往左", o.Count == 3 && o[0] == default && Math.Abs(o[1].X + 8) < 0.01 && Math.Abs(o[2].X + 20) < 0.01,
                    string.Join(" | ", o.Select(v => $"{v.X:0.#},{v.Y:0.#}")));
                await ShotAsync("motion-scene-right");
                scene.PointTo(new Point(0, scene.Bounds.Height));
                await ShotAsync("motion-scene-left");
                SystemMotion.Apply(Amber.Core.Layout.MotionPreference.On);
                scene.PointTo(new Point(scene.Bounds.Width, 0));
                await Task.Delay(300);
                Check("减少动画：场景不跟着指针动", scene.Offsets.All(v => v == default));
                SystemMotion.Apply(Amber.Core.Layout.MotionPreference.Off);
                // Standing still, a light/dark switch still repaints the dim over the scene.
                var drawn = scene.Renders;
                Application.Current!.RequestedThemeVariant = Application.Current.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                    ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
                await Task.Delay(400);
                Check("指针不动时切换深浅色：场景也重画了", scene.Renders > drawn, $"{drawn} → {scene.Renders}");
                Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
                await Task.Delay(200);
            }
            var privacyRow = window.GetVisualDescendants().OfType<PrivacyRow>().FirstOrDefault(r => r.IsEffectivelyVisible);
            Check("场景在内容下面：隐私行还在最上面看得见", privacyRow is not null && privacyRow.IsEffectivelyVisible);

            // 7. The skin figure on the "Me" page.
            var accounts = Page<AccountsViewModel>();
            if (!service.Accounts.Store.Accounts.Any(a => a.Name == "Robin"))
            {
                accounts.OfflineName = "Robin";
                accounts.AddOfflineCommand.Execute(null);
            }
            window.Width = 1280;
            window.Height = 800;
            main.Shell.Navigate("me");
            await WaitAsync(() => window.PageHost.GetVisualDescendants().OfType<SkinFigure>().Any(f => f.Skin is not null), 5);
            var figure = window.PageHost.GetVisualDescendants().OfType<SkinFigure>().FirstOrDefault();
            Check("“我”页面：离线账号也有一个转得动的小人（按名字画的）", figure is { IsEffectivelyVisible: true, Skin: not null } && figure.Skin.Pixels.SequenceEqual(PixelSkin.Generate("Robin").Pixels));
            if (figure is not null)
            {
                Check("小人停在侧一点的角度，读屏名字写怎么转", Math.Abs(figure.Angle - SkinTurntable.RestAngle) < 0.01
                    && Avalonia.Automation.AutomationProperties.GetName(figure) == Amber.Core.Localization.L.T("me.skin.figure"));
                await ShotAsync("motion-figure-rest");
                figure.Focus(NavigationMethod.Tab);
                figure.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
                figure.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
                Check("→ 转 15°（两下 30°）", Math.Abs(figure.Angle - (SkinTurntable.RestAngle + 30)) < 0.01, $"{figure.Angle:0.#}");
                figure.TurnBy(180 - figure.Angle);
                await ShotAsync("motion-figure-back");
                figure.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home });
                Check("Home 回到停着的角度", Math.Abs(figure.Angle - SkinTurntable.RestAngle) < 0.01);
                Check("指针放上去：自己慢慢转", figure.StartSpin());
                await Task.Delay(800);
                var turned = Math.Abs(figure.Angle - SkinTurntable.RestAngle);
                Check("转起来了", figure.Spinning && turned > 10, $"{turned:0.#}°");
                await ShotAsync("motion-figure-spin");
                figure.Settle();
                await WaitAsync(() => Math.Abs(figure.Angle - SkinTurntable.RestAngle) < 0.5, 3);
                Check("指针离开：停下并转回原来的角度", !figure.Spinning && Math.Abs(figure.Angle - SkinTurntable.RestAngle) < 0.5, $"{figure.Angle:0.#}");
                // Turned off while it turns: it stops and goes back, without waiting for the pointer to leave.
                Check("再转起来", figure.StartSpin() && figure.Spinning);
                settings.Effects3D = false;
                await WaitAsync(() => !figure.Spinning && Math.Abs(figure.Angle - SkinTurntable.RestAngle) < 0.5, 3);
                Check("转着的时候关掉立体效果：马上停下并回到原角度", !figure.Spinning && Math.Abs(figure.Angle - SkinTurntable.RestAngle) < 0.5, $"{figure.Angle:0.#}");
                settings.Effects3D = true;
                await Task.Delay(200);
                // A drag whose pointer capture is taken away ends there: moving the pointer afterwards doesn't turn it.
                var mouse = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
                figure.RaiseEvent(new PointerPressedEventArgs(figure, mouse, figure, new Point(20, 20), 0, props, KeyModifiers.None));
                figure.RaiseEvent(new PointerCaptureLostEventArgs(figure, mouse));
                var before = figure.Angle;
                figure.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, figure, mouse, figure, new Point(80, 20), 0, props, KeyModifiers.None));
                Check("拖动中指针被别处拿走：之后移动不再转", Math.Abs(figure.Angle - before) < 0.01, $"{before:0.#} → {figure.Angle:0.#}");
                figure.Settle();
                SystemMotion.Apply(Amber.Core.Layout.MotionPreference.On);
                Check("减少动画：不会自己转", !figure.StartSpin() && !figure.Spinning);
                figure.TurnBy(40);
                Check("减少动画：拖动和方向键照样能转（人在动，不是动画）", Math.Abs(figure.Angle - (SkinTurntable.RestAngle + 40)) < 0.01);
                figure.Settle();
                Check("减少动画：回到原角度是一下子，不是慢慢转", Math.Abs(figure.Angle - SkinTurntable.RestAngle) < 0.01);
                SystemMotion.Apply(Amber.Core.Layout.MotionPreference.Off);
            }
            window.Width = 600;
            window.Height = 420;
            await Task.Delay(600);
            await ShotAsync("motion-me-600");
            window.Width = 1280;
            window.Height = 800;
        }
        finally
        {
            ThemeService.Apply(null);
            SystemMotion.Apply(originalMode);
            settings.Effects3D = true;
            service.Settings.GameDirectory = originalDir;
            service.Settings.Instances = originalMeta;
            service.SaveAndReload();
        }
        await Task.Delay(400);
        Check("换回默认外观：没有场景了", Depth3D.SceneLayers.Count == 0 && Depth3D.Scene is null);
    }

    /// <summary>A theme pack folder in the report directory with "motion3d" numbers and a three-layer scene (far sky, hills, near trees).</summary>
    private ThemePack WriteScenePack()
    {
        var dir = Path.Combine(reportDir, "scene-pack");
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, "theme.json"), """{ "format": 2, "id": "selftest-scene", "name": "Scene", "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(dir, "tokens.json"), """
            {
              "motion3d": { "tilt": 6, "parallax": 20, "pageDepth": 0.02 },
              "slots": { "scene": { "layers": [
                { "image": "assets/sky.png", "depth": 0 },
                { "image": "assets/hills.png", "depth": 0.4 },
                { "image": "assets/trees.png", "depth": 1 }
              ], "dim": 0.35 } }
            }
            """);
        void draw(string name, Action<SKCanvas, int, int> paint)
        {
            const int w = 1920, h = 1080;
            using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                paint(canvas, w, h);
            }
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(dir, "assets", name), data.ToArray());
        }
        draw("sky.png", (c, w, h) =>
        {
            using var p = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h), [new SKColor(0x0B, 0x12, 0x2E), new SKColor(0x2D, 0x3F, 0x7A)], SKShaderTileMode.Clamp) };
            c.DrawRect(0, 0, w, h, p);
            using var star = new SKPaint { Color = new SKColor(0xF5, 0xF2, 0xEA) };
            var r = new Random(7);
            for (var i = 0; i < 120; i++) c.DrawRect(r.Next(w), r.Next(h / 2), 3, 3, star);
        });
        draw("hills.png", (c, w, h) =>
        {
            using var p = new SKPaint { Color = new SKColor(0x1D, 0x3B, 0x4C), IsAntialias = true };
            using var path = new SKPath();
            path.MoveTo(0, h * 0.7f);
            for (var x = 0; x <= w; x += 160) path.LineTo(x, h * (0.62f + 0.08f * MathF.Sin(x / 300f)));
            path.LineTo(w, h);
            path.LineTo(0, h);
            path.Close();
            c.DrawPath(path, p);
        });
        draw("trees.png", (c, w, h) =>
        {
            using var p = new SKPaint { Color = new SKColor(0x0E, 0x22, 0x1B) };
            for (var x = 40; x < w; x += 220)
            {
                c.DrawRect(x + 30, h * 0.72f, 20, h * 0.28f, p);
                c.DrawRect(x, h * 0.6f, 80, h * 0.16f, p);
            }
        });
        return ThemePackReader.ReadDirectory(dir);
    }
}
