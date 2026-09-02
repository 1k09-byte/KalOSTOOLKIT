using System;
using System.Numerics;
using KalOS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinUIEx;

namespace KalOS;

/// <summary>
/// Borderless notification banner that drops down from the top-right corner of
/// the screen. Runs the user's startup tasks hidden (progress bar + status),
/// reports toolkit updates, then slides back up and closes itself.
/// </summary>
public sealed partial class StartupBannerWindow : WindowEx
{
    private readonly StartupTasksService _startup;
    private readonly UpdateService _updateService;
    private readonly DiskCleanupService _cleanup;
    private readonly StartupSettings _settings;
    private readonly DispatcherQueueTimer _autoHideTimer;
    private readonly DispatcherQueueTimer _positionRetry;
    private DispatcherQueueTimer _statusCycler;
    private bool _dismissed;

    // Real, user-configured startup work reflected in Preview.
    private readonly List<string> _previewItems = new();
    private int _previewIndex;


    // Rotating status words shown while the banner is "working" (preview and any
    // indeterminate phase) so it reads as actively applying things.

    public StartupBannerWindow(StartupTasksService startup, UpdateService updateService, StartupSettings settings)
    {
        _startup = startup;
        _updateService = updateService;
        _cleanup = App.Services.GetRequiredService<DiskCleanupService>();
        _settings = settings;

        InitializeComponent();

        // This is a backdrop-less, borderless window, so translucent/theme fills
        // degrade toward black. Pin explicit opaque surfaces so the banner is
        // never a black box: Root carries the app-dark base color, and the card
        // (wallpaper on top, dark base beneath) is opaque until pixels exist.
        var baseColor = global::Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F);
        Root.Background = new SolidColorBrush(baseColor);
        BannerCard.Background = new SolidColorBrush(baseColor);

        ApplyBackgroundImage();

        // Borderless + always-on-top via WinUIEx WindowEx properties.
        this.IsTitleBarVisible = false;
        this.IsAlwaysOnTop = true;
        this.IsShownInSwitchers = false;

        _autoHideTimer = DispatcherQueue.CreateTimer();
        _autoHideTimer.Interval = TimeSpan.FromSeconds(6);
        _autoHideTimer.IsRepeating = false;
        _autoHideTimer.Tick += (_, _) => DismissAsync();

        // Positioning retries repeat a few times: right after Windows login the
        // display area may not be ready yet, and AppWindow.Move can throw.
        _positionRetry = DispatcherQueue.CreateTimer();
        _positionRetry.Interval = TimeSpan.FromMilliseconds(150);
        _positionRetry.IsRepeating = true;
        _positionRetry.Tick += (_, _) =>
        {
            if (++_positionAttempts >= 10) _positionRetry.Stop();
            else PositionAtTopCenter();
        };

        _statusCycler = DispatcherQueue.CreateTimer();
        _statusCycler.Interval = TimeSpan.FromMilliseconds(450);
        _statusCycler.IsRepeating = true;
        _statusCycler.Tick += (_, _) => CycleStatusWord();

        InitBannerVisual();

        // Position on activation. Right after login the AppWindow may not be
        // usable yet — AppWindow.Move throws "The parameter is incorrect" — so
        // this handler must never throw: guard everything and let the retry
        // timer land the final position instead.
        Activated += (_, _) =>
        {
            try
            {
                if (AppWindow is null || Content is null) return;
                PositionAtTopCenter();
            }
            catch { /* not ready yet — retry timer covers it */ }
        };

        // Drop the wallpaper decode on close so an in-flight BitmapImage
        // completion can't touch the destroyed visual tree during teardown —
        // one source of the native 0xc0000005 close-crash dialog.
        Closed += (_, _) =>
        {
            try { BackgroundImage.Source = null; } catch { }
        };
    }

    /// <summary>Shows the banner and starts its work (tasks + optional update check).</summary>
    public void Run()
    {
        Activate();
        PositionAtTopCenter();
        SlideIn();

        // Startup sequence, in order: check updates → scan temp files → clean
        // them → run configured startup commands.
        _ = RunStartupSequenceAsync();
    }

    /// <summary>
    /// Runs the startup work strictly in order: check for updates, scan temp
    /// files, clean them, then run the configured startup commands. Each step
    /// waits for the previous one to finish so the banner reflects real progress.
    /// </summary>
    private async System.Threading.Tasks.Task RunStartupSequenceAsync()
    {
        // 1. Check for updates — consumer builds only. The dev/edit tool is
        //    built from the same repo and often runs a NEWER version than the
        //    published release, so letting it check would nag about "updates"
        //    (or even trigger the rollback path) against itself. Skip entirely.
#if CONSUMER_BUILD
        if (_settings.CheckUpdatesAtStartup)
        {
            TaskProgress.IsIndeterminate = true;
            StatusText.Text = "Checking for updates…";
            DetailText.Text = "Looking for a newer KalOS version";
            DetailText.Visibility = Visibility.Visible;

            var info = await _updateService.CheckForUpdatesAsync();
            if (info != null)
            {
                StatusText.Text = $"KalOS {info.Version} is available.";
                DetailText.Text = "Open the tool to update.";
                _autoHideTimer.Stop();
                // Update notice: hold 3 seconds so it's readable, then the
                // banner closes itself and the process exits (Closed handler).
                _autoHideTimer.Interval = TimeSpan.FromSeconds(3);
                _autoHideTimer.Start();
                return; // an update is on offer — show it briefly, then close
            }
        }
#endif // CONSUMER_BUILD

        // 2. Scan temp files (no deletion yet).
        TaskProgress.IsIndeterminate = true;
        StatusText.Text = "Scanning temp files…";
        DetailText.Text = "Measuring temporary and junk files";
        DetailText.Visibility = Visibility.Visible;

        IReadOnlyList<Services.CleanupCategory> categories = Array.Empty<Services.CleanupCategory>();
        try
        {
            categories = await _cleanup.ScanAsync();
        }
        catch
        {
            categories = Array.Empty<Services.CleanupCategory>();
        }
        long scanned = 0;
        foreach (var c in categories) scanned += c.CleanableBytes;

        // 3. Clean the scanned temp files.
        TaskProgress.IsIndeterminate = true;
        StatusText.Text = $"Cleaning temp files ({CleanupCategory.FormatBytes(scanned)})…";
        DetailText.Text = "Removing temporary and junk files";

        long freed = 0;
        try
        {
            var (_, result) = await _cleanup.CleanAsync(categories, progress: null);
            freed = result;
        }
        catch
        {
            freed = 0;
        }

        StatusText.Text = freed > 0
            ? $"Freed {CleanupCategory.FormatBytes(freed)} of temp files."
            : "No temp files to clean.";
        DetailText.Visibility = Visibility.Collapsed;

        // 4. Run configured startup commands.
        int total = 0;
        foreach (var t in _settings.Tasks)
        {
            if (t.Enabled && !string.IsNullOrWhiteSpace(t.Command)) total++;
        }
        if (total > 0)
        {
            await RunTasksAsync(total);
        }

        TaskProgress.IsIndeterminate = false;
        ScheduleAutoHide();
    }

    /// <summary>
    /// Shows the banner as a non-invoking preview so the user can see exactly
    /// what will happen at login: the configured startup commands and the update
    /// check. It does not run any of them — it just demonstrates the surface.
    /// </summary>
    public void Preview()
    {
        Activate();
        PositionAtTopCenter();
        SlideIn();

        _previewItems.Clear();
        if (_settings.CheckUpdatesAtStartup) _previewItems.Add("Check for KalOS updates");
        _previewItems.Add("Scan then clean temp and junk files");
        foreach (var t in _settings.Tasks)
        {
            if (t.Enabled && !string.IsNullOrWhiteSpace(t.Command))
                _previewItems.Add(t.Command.Trim());
        }

        // Preview: show the progress bar as an animated indeterminate bar.
        TaskProgress.IsIndeterminate = true;
        _previewIndex = 0;
        CycleStatusWord();
        DetailText.Visibility = Visibility.Visible;
        _statusCycler.Start();

        ScheduleAutoHide();
    }

    /// <summary>Shows the next real step the banner would run (task command or update check).</summary>
    private void CycleStatusWord()
    {
        if (DetailText == null) return;

        if (_previewItems.Count == 0)
        {
            StatusText.Text = "Startup preview";
            DetailText.Text = "Nothing extra will run at login.";
            return;
        }

        // Advance through the real steps once, in order, then stop — no endless
        // looping between "checking updates" and "cleaning files".
        if (_previewIndex >= _previewItems.Count)
        {
            _statusCycler.Stop();
            StatusText.Text = "All done — this runs once at login.";
            DetailText.Text = DescribeStartupPlan();
            return;
        }

        var item = _previewItems[_previewIndex++];
        if (item.Equals("Check for KalOS updates", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Checks for KalOS updates";
            DetailText.Text = "Step " + _previewIndex + " of " + _previewItems.Count + ": " + item;
        }
        else if (item.Equals("Scan then clean temp and junk files", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Cleans up temp and junk files";
            DetailText.Text = item;
        }
        else
        {
            StatusText.Text = "Running: ";
        }
        if (!item.Equals("Check for KalOS updates", StringComparison.OrdinalIgnoreCase))
        {
            DetailText.Text = item;
        }
    }

    /// <summary>
    /// Builds the preview's closing summary from the ACTUAL configuration, so it
    /// never advertises steps that are disabled (e.g. mentions "check updates"
    /// when the update check is turned off).
    /// </summary>
    private string DescribeStartupPlan()
    {
        var parts = new List<string>();
        if (_settings.CheckUpdatesAtStartup) parts.Add("checks for updates");
        parts.Add("scans & cleans temp files");
        int commandCount = 0;
        foreach (var t in _settings.Tasks)
        {
            if (t.Enabled && !string.IsNullOrWhiteSpace(t.Command)) commandCount++;
        }
        if (commandCount > 0)
            parts.Add($"runs {commandCount} startup command{(commandCount == 1 ? "" : "s")}");

        string joined = string.Join(", ", parts);
        return char.ToUpperInvariant(joined[0]) + joined[1..] + ".";
    }

    private async System.Threading.Tasks.Task RunTasksAsync(int total)
    {
        await _startup.RunTasksAsync(_settings.Tasks, (index, count, command) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TaskProgress.Maximum = Math.Max(count, 1);
                TaskProgress.Value = index;
                StatusText.Text = count > 0
                    ? $"Running startup tasks… ({index + 1}/{count})"
                    : "Running startup tasks…";
                DetailText.Text = command;
                DetailText.Visibility = string.IsNullOrEmpty(command)
                    ? Visibility.Collapsed : Visibility.Visible;
            });
        });

        DispatcherQueue.TryEnqueue(() =>
        {
            TaskProgress.Value = TaskProgress.Maximum;
            StatusText.Text = $"Finished {total} startup task{(total == 1 ? "" : "s")}.";
            DetailText.Visibility = Visibility.Collapsed;
        });
    }

    private void ScheduleAutoHide()
    {
        _autoHideTimer.Stop();
        _autoHideTimer.Interval = TimeSpan.FromSeconds(6);
        _autoHideTimer.Start();
    }

    // ── Background wallpaper ──────────────────────────────────────────────

    /// <summary>
    /// Loads the same background wallpaper the main window uses (from saved
    /// settings) and shows it inside the card. The wallpaper lives inside
    /// <see cref="BannerCard"/>, so it animates together with the text — the
    /// whole banner is one unit. Falls back to a dark surface when unset.
    /// </summary>
    private void ApplyBackgroundImage()
    {
        try
        {
            var settings = UpdateService.LoadSettings();
            if (string.IsNullOrEmpty(settings.BackgroundImagePath) || !System.IO.File.Exists(settings.BackgroundImagePath))
            {
                // No wallpaper — keep the card on the default layered surface.
                BackgroundImage.Source = null;
                BackgroundImage.Visibility = Visibility.Collapsed;
                BackgroundOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            var uri = new Uri(settings.BackgroundImagePath, UriKind.Absolute);
            var bitmap = new BitmapImage(uri);
            BackgroundImage.Source = bitmap;
            BackgroundImage.Opacity = Math.Clamp(settings.BackgroundImageOpacity, 0.15, 0.9);
            BackgroundImage.Stretch = settings.BackgroundImageFit switch
            {
                "Uniform" => Stretch.Uniform,
                "Fill" => Stretch.Fill,
                "None" => Stretch.None,
                _ => Stretch.UniformToFill
            };
            BackgroundImage.HorizontalAlignment = settings.BackgroundImageHorizontalAlignment switch
            {
                "Left" => HorizontalAlignment.Left,
                "Right" => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center
            };
            BackgroundImage.VerticalAlignment = settings.BackgroundImageVerticalAlignment switch
            {
                "Top" => VerticalAlignment.Top,
                "Bottom" => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Center
            };

            // Keep the wallpaper + overlay hidden until the bitmap is actually
            // decoded; revealing instantly would flash the card as an empty
            // black slab while the image loads. Appears trickling in as soon as
            // the pixels are ready.
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundOverlay.Visibility = Visibility.Collapsed;
            bitmap.ImageOpened += (_, _) =>
            {
                if (BackgroundImage.Source == bitmap)
                {
                    BackgroundImage.Visibility = Visibility.Visible;
                    BackgroundOverlay.Visibility = Visibility.Visible;
                }
            };
            bitmap.ImageFailed += (_, _) =>
            {
                BackgroundImage.Source = null;
                BackgroundImage.Visibility = Visibility.Collapsed;
                BackgroundOverlay.Visibility = Visibility.Collapsed;
            };
        }
        catch
        {
            BackgroundImage.Source = null;
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ── Positioning / animation ──────────────────────────────────────────

    private static readonly System.Numerics.Vector3 HiddenOffset = new(0, 26, 0);
    private static readonly System.Numerics.Vector3 ShownOffset = new(0, 0, 0);
    private const float HiddenOpacity = 0f;
    private const float ShownOpacity = 1f;
    private Microsoft.UI.Composition.Visual _bannerVisual = null!;
    private bool _dismissAnimating;
    private int _positionAttempts;

    private void ComputeBannerRect()
    {
        try
        {
            DisplayArea area;
            try
            {
                area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            }
            catch
            {
                area = DisplayArea.Primary;
            }

            var areaRect = area.WorkArea;
            int width = 560;
            int x = areaRect.X + (areaRect.Width - width) / 2;
            AppWindow?.Move(new PointInt32(x, areaRect.Y + 8));

            // Positioned successfully — stop retrying.
            _positionRetry.Stop();
        }
        catch
        {
            // Display/window not ready yet (common right after login) — retry
            // a few times so the final position still lands.
            _positionAttempts = 0;
            _positionRetry.Stop();
            _positionRetry.Start();
        }
    }

    /// <summary>Positions the window top-center (safe: retries until the handle is ready).</summary>
    private void PositionAtTopCenter() => ComputeBannerRect();

    /// <summary>
    /// Installs the visual we animate later. The whole banner (wallpaper + text
    /// + progress) moves together as one GPU-composited unit; the slide is short
    /// and paired with a fade so no dark surface is ever exposed mid-animation.
    /// </summary>
    private void InitBannerVisual()
    {
        // Animate BannerCard over the opaque dark Root: the same-colored surface
        // behind it means the exposed strip mid-slide is invisible (never black).
        _bannerVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(BannerCard);
        _bannerVisual.Opacity = HiddenOpacity;
    }

    /// <summary>Quick drop + fade-in of the whole banner via the compositor.</summary>
    private void SlideIn()
    {
        TryGetBannerVisual();
        if (_bannerVisual == null) { _bannerVisual!.Opacity = ShownOpacity; return; }

        _dismissAnimating = false;
        _bannerVisual.Offset = HiddenOffset;
        _bannerVisual.Opacity = HiddenOpacity;

        var compositor = _bannerVisual.Compositor;
        var easeIn = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.15f, 0.9f), new System.Numerics.Vector2(0.35f, 1f));

        var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
        offsetAnim.Duration = TimeSpan.FromMilliseconds(260);
        offsetAnim.InsertKeyFrame(0.0f, HiddenOffset);
        offsetAnim.InsertKeyFrame(1.0f, ShownOffset, easeIn);
        _bannerVisual.StartAnimation("Offset", offsetAnim);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.Duration = TimeSpan.FromMilliseconds(220);
        opacityAnim.InsertKeyFrame(0.0f, HiddenOpacity);
        opacityAnim.InsertKeyFrame(1.0f, ShownOpacity);
        _bannerVisual.StartAnimation("Opacity", opacityAnim);
    }

    /// <summary>Lifts + fades the banner out, then closes the window.</summary>
    private void SlideOut()
    {
        TryGetBannerVisual();
        if (_bannerVisual == null) { Close(); return; }

        _dismissAnimating = true;
        var compositor = _bannerVisual.Compositor;
        var easeOut = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.4f, 0f), new System.Numerics.Vector2(0.6f, 1f));

        var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
        offsetAnim.Duration = TimeSpan.FromMilliseconds(240);
        offsetAnim.InsertKeyFrame(0.0f, ShownOffset);
        offsetAnim.InsertKeyFrame(1.0f, HiddenOffset, easeOut);
        _bannerVisual.StartAnimation("Offset", offsetAnim);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.Duration = TimeSpan.FromMilliseconds(200);
        opacityAnim.InsertKeyFrame(0.0f, ShownOpacity);
        opacityAnim.InsertKeyFrame(1.0f, HiddenOpacity);
        _bannerVisual.StartAnimation("Opacity", opacityAnim);

        // Close once the movement finishes (guarded by _dismissAnimating so a
        // reinvoked Dismiss can't double-fire).
        ScheduleCloseAfterAnimation();
    }

    private void TryGetBannerVisual()
    {
        if (_bannerVisual == null)
        {
            _bannerVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(BannerCard);
        }
    }

    private void DismissAsync()
    {
        if (_dismissed || _dismissAnimating) return;
        _dismissed = true;
        _autoHideTimer.Stop();
        _statusCycler.Stop();
        SlideOut();
    }

    private async void ScheduleCloseAfterAnimation()
    {
        await System.Threading.Tasks.Task.Delay(260);
        try { Close(); } catch { }
    }
}
