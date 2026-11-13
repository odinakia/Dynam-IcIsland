using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;
using DynamicIsland.UI;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    private enum IslandMode
    {
        Compact,
        Expanded,
        Drop,
    }

    private const double CompactWidth = 208;
    private const double CompactHeight = 34;
    private const double ExpandedWidth = 448;
    private const double ExpandedHeightFull = 218;
    private const double ExpandedHeightMinimal = 153;
    private const double DropWidth = 448;
    private const double DropHeightEmpty = 100;
    private const double DropHeightWithShelf = 180;

    private static readonly Geometry PlayGeometry = Geometry.Parse("M2,1 L10,6 L2,11 Z");
    private static readonly Geometry PauseGeometry = Geometry.Parse("M1,1 H4.4 V11 H1 Z M6.6,1 H10 V11 H6.6 Z");
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly Settings _settings;
    private readonly NowPlayingService _music = new();
    private readonly WeatherService _weather;
    private readonly FullscreenWatcher _fullscreen = new();
    private readonly ObservableCollection<ShelfItem> _shelf = new();

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _peekTimer;
    private readonly DispatcherTimer _topmostTimer;
    private readonly EqualizerAnimator _eq;

    private HwndSource? _hwndSource;
    private IslandMode _mode = IslandMode.Compact;
    private bool _pinned;
    private bool _hovering;
    private bool _hidden;
    private bool _trayHidden;
    private bool _ready;
    private DateTime _suppressPeekUntil = DateTime.MinValue;
    private int _batteryTick;

    public MainWindow(Settings settings)
    {
        _settings = settings;
        _weather = new WeatherService(settings);

        InitializeComponent();

        ShelfList.ItemsSource = _shelf;
        _eq = new EqualizerAnimator(Eq1, Eq2, Eq3, Eq4, EqX1, EqX2, EqX3, EqX4, EqX5);

        ArtBorder.SizeChanged += (_, _) =>
        {
            var width = ArtBorder.ActualWidth;
            var height = ArtBorder.ActualHeight;
            ArtBorder.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 14, 14);
        };

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += (_, _) => TickClock();

        _collapseTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_pinned || _hovering || _mode == IslandMode.Drop) return;
            if (!Island.IsMouseOver) SetMode(IslandMode.Compact);
        };

        _peekTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3.6),
        };
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            if (_pinned || _hovering) return;
            if (!Island.IsMouseOver) SetMode(IslandMode.Compact);
        };

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();

        foreach (var path in settings.Shelf)
        {
            if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                _shelf.Add(new ShelfItem(path));
            if (_shelf.Count >= 20) break;
        }

        UpdateShelfUi();
        UpdateWeatherUi();
        UpdateMusicUi();
        UpdateBattery();
        TickClock();
        ApplyDisplaySettings(false);
        ApplyDisplaySettings(false);

        Loaded += OnLoaded;
        Closing += (_, _) => ShutdownServices();
    }

    // ───────────────────────────── жизненный цикл ─────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionAtTopCenter();

        _music.Updated += OnMusicUpdated;
        _music.TrackChanged += OnTrackChanged;
        _weather.Updated += OnWeatherUpdated;
        _fullscreen.Changed += OnFullscreenChanged;

        _music.Start();
        _weather.Start();
        _clockTimer.Start();
        _topmostTimer.Start();

        UpdateMusicUi();
        UpdateWeatherUi();
        UpdateBattery();
        TickClock();

        _suppressPeekUntil = DateTime.UtcNow.AddSeconds(4);
        _ready = true;
        NativeMethods.TrimWorkingSet();

        // отладочный запуск сразу в раскрытом виде (не влияет на обычную работу)
        var startState = Environment.GetEnvironmentVariable("ISLAND_START_STATE");
        if (startState == "expanded") SetMode(IslandMode.Expanded, animate: false);
        else if (startState == "drop") SetMode(IslandMode.Drop, animate: false);
        else if (startState == "menu") Dispatcher.BeginInvoke(ShowSettingsMenu, DispatcherPriority.Background);
    }

    public void PositionAtTopCenter()
    {
        Left = Math.Round((SystemParameters.PrimaryScreenWidth - Width) / 2);
        Top = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE,
            new IntPtr(exStyle | NativeMethods.WS_EX_TOOLWINDOW));

        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            var value = lParam.ToInt64();
            var screenX = unchecked((short)(value & 0xFFFF));
            var screenY = unchecked((short)((value >> 16) & 0xFFFF));
            var point = PointFromScreen(new Point(screenX, screenY));
            if (!IsPointOverIsland(point))
            {
                handled = true;
                return new IntPtr(NativeMethods.HTTRANSPARENT);
            }
        }
        else if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // Клик по островку не должен забирать фокус у игры или фильма.
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    private bool IsPointOverIsland(Point point)
    {
        if (Visibility != Visibility.Visible || _hidden) return false;
        try
        {
            var origin = Island.TranslatePoint(new Point(0, 0), this);
            var rect = new Rect(origin.X, origin.Y, Island.ActualWidth, Island.ActualHeight);
            return rect.Contains(point);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ReassertTopmost()
    {
        if (Visibility != Visibility.Visible || _hidden) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch
        {
            // ignore
        }
    }

    private bool _shutdown;

    public void ShutdownServices()
    {
        if (_shutdown) return;
        _shutdown = true;

        _clockTimer.Stop();
        _collapseTimer.Stop();
        _peekTimer.Stop();
        _topmostTimer.Stop();

        _fullscreen.Changed -= OnFullscreenChanged;
        _weather.Updated -= OnWeatherUpdated;
        _music.Updated -= OnMusicUpdated;
        _music.TrackChanged -= OnTrackChanged;

        _fullscreen.Dispose();
        _weather.Dispose();
        _music.Dispose();
        _eq.Dispose();

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    public void ApplySettings()
    {
        if (!_settings.HideInFullscreen && _hidden && !_trayHidden) SlideIn();
    }

    public void ReloadWeather() => _ = _weather.RefreshAsync(force: true);

    public void ToggleFromTray()
    {
        if (Visibility == Visibility.Visible && !_hidden)
        {
            _trayHidden = true;
            SlideOut();
        }
        else
        {
            if (_settings.HideInFullscreen && _fullscreen.IsFullscreen) return;
            _trayHidden = false;
            SlideIn();
        }
    }

    // ───────────────────────────── режимы и анимации ─────────────────────────────

    private double ExpandedHeight => StatusVisible ? ExpandedHeightFull : ExpandedHeightMinimal;

    private double DropHeight => _shelf.Count > 0 ? DropHeightWithShelf : DropHeightEmpty;

    private void SetMode(IslandMode mode, bool animate = true)
    {
        if (mode == _mode && animate) return;

        _mode = mode;

        var width = mode switch
        {
            IslandMode.Compact => CompactWidth,
            IslandMode.Drop => DropWidth,
            _ => ExpandedWidth,
        };

        var height = mode switch
        {
            IslandMode.Compact => CompactHeight,
            IslandMode.Drop => DropHeight,
            _ => ExpandedHeight,
        };
        AnimateSize(width, height, animate, mode == IslandMode.Compact);

        switch (mode)
        {
            case IslandMode.Compact:
                HideCard(ExpandedLayer, ExpandedScale, ExpandedSlide, animate);
                HideCard(DropLayer, DropScale, DropSlide, animate);
                ShowCompact(animate);
                break;

            case IslandMode.Drop:
                HideCompact(animate);
                HideCard(ExpandedLayer, ExpandedScale, ExpandedSlide, animate);
                ShowCard(DropLayer, DropScale, DropSlide, animate, 0.95, 10, 180, 40);
                break;

            default:
                HideCard(DropLayer, DropScale, DropSlide, animate);
                HideCompact(animate);
                ShowCard(ExpandedLayer, ExpandedScale, ExpandedSlide, animate, 0.94, 12, 250, 40);
                break;
        }

        UpdateEq();
    }

    private void AnimateSize(double width, double height, bool animate, bool compacting)
    {
        if (!animate)
        {
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            Island.Width = width;
            Island.Height = height;
            return;
        }

        // мягкое симметричное ускорение/замедление — без рывков на старте
        IEasingFunction easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var duration = compacting ? 260 : 480;

        AnimateTo(Island, WidthProperty, Island.ActualWidth > 1 ? Island.ActualWidth : width, width, duration, easing);
        AnimateTo(Island, HeightProperty, Island.ActualHeight > 1 ? Island.ActualHeight : height, height, duration, easing);
    }

    private void ShowCompact(bool animate)
    {
        var layer = CompactLayer;
        var token = new object();

        layer.Visibility = Visibility.Visible;
        layer.Tag = token;

        if (!animate)
        {
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = 1;
            ResetScale(CompactScale);
            layer.Effect = null;
            return;
        }

        FadeTo(layer, 1, 260, 10);
        AnimateScale(CompactScale, 0.92, 1, 420, EasingMode.EaseOut, 10);
    }

    private void HideCompact(bool animate)
    {
        var layer = CompactLayer;

        if (!animate)
        {
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = 0;
            layer.Visibility = Visibility.Hidden;
            ResetScale(CompactScale);
            layer.Effect = null;
            return;
        }

        if (layer.Visibility != Visibility.Visible) return;

        var token = new object();
        layer.Tag = token;

        FadeTo(layer, 0, 120, 0, () =>
        {
            if (!ReferenceEquals(layer.Tag, token)) return;
            layer.Visibility = Visibility.Hidden;
            ResetScale(CompactScale);
        });

        AnimateScale(CompactScale, CompactScale.ScaleX, 0.94, 180, EasingMode.EaseInOut, 0);
    }

    private static void ShowCard(FrameworkElement layer, ScaleTransform scale, TranslateTransform slide,
        bool animate, double fromScale, double fromOffset, double fadeMs, double delay)
    {
        layer.Visibility = Visibility.Visible;

        if (!animate)
        {
            layer.Tag = null;
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = 1;
            ResetScale(scale);
            ResetSlide(slide);
            layer.Effect = null;
            return;
        }

        var token = new object();
        layer.Tag = token;

        FadeTo(layer, 1, fadeMs, delay);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = fromScale;
        AnimateScale(scale, fromScale, 1, 500, EasingMode.EaseOut, delay);

        slide.BeginAnimation(TranslateTransform.YProperty, null);
        slide.Y = fromOffset;
        AnimateSlide(slide, fromOffset, 0, 520, EasingMode.EaseOut, delay);
    }

    private static void HideCard(FrameworkElement layer, ScaleTransform scale, TranslateTransform slide, bool animate)
    {
        if (!animate)
        {
            layer.Tag = null;
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = 0;
            layer.Visibility = Visibility.Hidden;
            ResetScale(scale);
            ResetSlide(slide);
            layer.Effect = null;
            return;
        }

        if (layer.Visibility != Visibility.Visible) return;

        var token = new object();
        layer.Tag = token;

        FadeTo(layer, 0, 130, 0, () =>
        {
            if (!ReferenceEquals(layer.Tag, token)) return;
            layer.Visibility = Visibility.Hidden;
            ResetScale(scale);
            ResetSlide(slide);
        });

        AnimateScale(scale, scale.ScaleX, 0.97, 190, EasingMode.EaseInOut, 0);
        AnimateSlide(slide, slide.Y, 6, 190, EasingMode.EaseInOut, 0);
    }

    // ── плавные анимации ──

    private static void AnimateScale(ScaleTransform transform, double from, double to, double milliseconds,
        EasingMode mode, double delay = 0, Action? completed = null)
    {
        var easing = new CubicEase { EasingMode = mode };
        AnimateTo(transform, ScaleTransform.ScaleXProperty, from, to, milliseconds, easing, delay, completed);
        AnimateTo(transform, ScaleTransform.ScaleYProperty, from, to, milliseconds, easing, delay);
    }

    private static void AnimateSlide(TranslateTransform transform, double from, double to, double milliseconds,
        EasingMode mode, double delay = 0, Action? completed = null)
    {
        var easing = new CubicEase { EasingMode = mode };
        AnimateTo(transform, TranslateTransform.YProperty, from, to, milliseconds, easing, delay, completed);
    }

    private static void AnimateTo(DependencyObject target, DependencyProperty property, double from, double to,
        double milliseconds, IEasingFunction easing, double delay = 0, Action? completed = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        };

        animation.Completed += (_, _) =>
        {
            target.SetValue(property, to);
            ((IAnimatable)target).BeginAnimation(property, null);
            completed?.Invoke();
        };

        ((IAnimatable)target).BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetScale(ScaleTransform transform)
    {
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        transform.ScaleX = transform.ScaleY = 1;
    }

    private static void ResetSlide(TranslateTransform transform)
    {
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = 0;
    }

    private static void FadeTo(UIElement element, double target, double milliseconds, double delay = 0, Action? completed = null)
    {
        if (milliseconds <= 0)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = target;
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(element.Opacity, target, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        animation.Completed += (_, _) =>
        {
            if (Math.Abs(element.Opacity - target) < 0.01)
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = target;
            }

            completed?.Invoke();
        };

        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void Island_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var radius = Math.Min(Island.ActualHeight / 2, 34);
        Island.CornerRadius = new CornerRadius(radius);

        var width = IslandContent.ActualWidth;
        var height = IslandContent.ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (IslandContent.Clip is RectangleGeometry geometry)
        {
            geometry.Rect = new Rect(0, 0, width, height);
            geometry.RadiusX = geometry.RadiusY = radius;
        }
        else
        {
            IslandContent.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        }
    }

    // ───────────────────────────── мышь и состояние ─────────────────────────────

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        _collapseTimer.Stop();
        _peekTimer.Stop();

        if (_hidden) return;
        if (_mode == IslandMode.Compact) SetMode(IslandMode.Expanded);
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        if (_mode == IslandMode.Drop) return;
        if (_pinned) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == IslandMode.Drop) return;

        e.Handled = true;
        _pinned = !_pinned;

        if (_pinned)
        {
            _peekTimer.Stop();
            SetMode(IslandMode.Expanded);
        }
        else if (!Island.IsMouseOver)
        {
            SetMode(IslandMode.Compact);
        }
    }

    private void Island_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_hidden || _mode == IslandMode.Drop) return;
        ShowSettingsMenu();
    }

    private bool StatusVisible =>
        _settings.ShowWeather || _settings.ShowClock || _settings.ShowBattery;

    private void ApplyDisplaySettings(bool animate)
    {
        StatusBlock.Visibility = StatusVisible ? Visibility.Visible : Visibility.Collapsed;

        WeatherGroup.Visibility = _settings.ShowWeather ? Visibility.Visible : Visibility.Collapsed;
        ClockGroup.Visibility = _settings.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        BatteryGroup.Visibility = _settings.ShowBattery ? Visibility.Visible : Visibility.Collapsed;

        WeatherDivider.Visibility = _settings.ShowWeather && (_settings.ShowClock || _settings.ShowBattery)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClockDivider.Visibility = _settings.ShowBattery && (_settings.ShowWeather || _settings.ShowClock)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var showWeatherInPill = _settings.ShowWeather;
        PillWeatherIcon.Visibility = showWeatherInPill ? Visibility.Visible : Visibility.Collapsed;
        PillWeatherTemp.Visibility = showWeatherInPill ? Visibility.Visible : Visibility.Collapsed;

        if (_mode == IslandMode.Expanded)
            AnimateSize(ExpandedWidth, ExpandedHeight, animate, false);
    }

    private void ShowSettingsMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = Island,
            Placement = PlacementMode.Custom,
            StaysOpen = false,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
            {
                new CustomPopupPlacement(
                    new Point(Math.Max(0, (targetSize.Width - popupSize.Width) / 2), targetSize.Height + 8),
                    PopupPrimaryAxis.Horizontal),
            },
        };

        menu.Items.Add(CheckMenuItem("Погода", _settings.ShowWeather, value =>
        {
            _settings.ShowWeather = value;
            SaveAndApplyDisplay();
        }));

        menu.Items.Add(CheckMenuItem("Часы и дата", _settings.ShowClock, value =>
        {
            _settings.ShowClock = value;
            SaveAndApplyDisplay();
        }));

        menu.Items.Add(CheckMenuItem("Батарея", _settings.ShowBattery, value =>
        {
            _settings.ShowBattery = value;
            SaveAndApplyDisplay();
        }));

        menu.Items.Add(new Separator());

        var city = new MenuItem { Header = "Город для погоды…", Style = MenuItemStyle };
        city.Click += (_, _) => EditCity();
        menu.Items.Add(city);

        menu.Items.Add(new Separator());

        menu.Items.Add(CheckMenuItem("Скрывать в полноэкранном режиме", _settings.HideInFullscreen, value =>
        {
            _settings.HideInFullscreen = value;
            _settings.Save();
            ApplySettings();
        }));

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Выход", Style = MenuItemStyle };
        exit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(exit);

        menu.IsOpen = true;
    }

    private static Style MenuItemStyle =>
        (Style)System.Windows.Application.Current.Resources["IslandMenuItem"];

    private static MenuItem CheckMenuItem(string header, bool isChecked, Action<bool> changed)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = isChecked,
            StaysOpenOnClick = true,
            Style = MenuItemStyle,
        };
        item.Click += (_, _) => changed(item.IsChecked);
        return item;
    }

    private void SaveAndApplyDisplay()
    {
        _settings.Save();
        ApplyDisplaySettings(true);
        UpdateWeatherUi();
    }

    private void EditCity()
    {
        var dialog = new CityDialog(_settings.City);
        if (dialog.ShowDialog() == true)
        {
            _settings.City = dialog.City;
            _settings.Save();
            ReloadWeather();
        }
    }

    private void OnTrackChanged()
    {
        if (!_ready || !_settings.PeekOnTrackChange) return;
        if (_pinned || _hovering || _hidden) return;
        if (DateTime.UtcNow < _suppressPeekUntil) return;
        if (_mode != IslandMode.Compact) return;

        SetMode(IslandMode.Expanded);
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    // ───────────────────────────── перетаскивание файлов ─────────────────────────────

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _hidden)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _collapseTimer.Stop();
        _peekTimer.Stop();
        SetMode(IslandMode.Drop);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        if (_mode != IslandMode.Drop) return;
        SetMode(_pinned ? IslandMode.Expanded : IslandMode.Compact);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                var added = AddFiles(paths);
                if (added > 0)
                {
                    _pinned = false;
                    SetMode(IslandMode.Expanded);
                    _peekTimer.Stop();
                    _peekTimer.Start();
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("drop: " + ex.Message);
        }
        finally
        {
            e.Handled = true;
        }
    }

    private int AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (_shelf.Any(existing => string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            if (_shelf.Count >= 20) _shelf.RemoveAt(0);
            _shelf.Add(new ShelfItem(path));
            added++;
        }

        if (added > 0)
        {
            UpdateShelfUi();
            PersistShelf();
        }

        return added;
    }

    private void PersistShelf() => _settings.SaveShelf(_shelf.Select(item => item.Path));

    private void UpdateShelfUi()
    {
        var hasFiles = _shelf.Count > 0;
        DropShelf.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ShelfCount.Text = hasFiles ? _shelf.Count.ToString(CultureInfo.InvariantCulture) : "";
    }

    /// <summary>Очистка полки из меню в трее (во время перетаскивания кликнуть нельзя).</summary>
    public void ClearShelf()
    {
        if (_shelf.Count == 0) return;
        _shelf.Clear();
        UpdateShelfUi();
        PersistShelf();
    }

    private void ShelfScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ShelfScroll.ScrollToHorizontalOffset(ShelfScroll.HorizontalOffset - e.Delta * 0.55);
        e.Handled = true;
    }

    // ───────────────────────────── музыка ─────────────────────────────

    private void OnMusicUpdated()
    {
        UpdateMusicUi();
        UpdateProgress();
    }

    private void UpdateMusicUi()
    {
        var hasTrack = _music.HasSession;

        TrackTitle.Text = hasTrack
            ? (string.IsNullOrWhiteSpace(_music.Title) ? "Без названия" : _music.Title)
            : "Ничего не играет";
        TrackArtist.Text = hasTrack
            ? (string.IsNullOrWhiteSpace(_music.Artist) ? "неизвестный исполнитель" : _music.Artist)
            : "включите музыку в любом плеере";
        TrackSource.Text = hasTrack && !string.IsNullOrWhiteSpace(_music.Source)
            ? _music.Source.ToUpperInvariant()
            : "МЕДИА";

        ArtImage.Source = hasTrack ? _music.Artwork : null;
        ArtPlaceholder.Visibility = hasTrack && _music.Artwork != null
            ? Visibility.Collapsed
            : Visibility.Visible;

        PlayIcon.Data = _music.IsPlaying ? PauseGeometry : PlayGeometry;

        UpdateProgress();
        UpdateEq();
    }

    private void UpdateProgress()
    {
        if (!_music.HasSession)
        {
            ProgressScale.ScaleX = 0;
            PositionText.Text = "";
            DurationText.Text = "";
            return;
        }

        var duration = _music.Duration;
        var position = _music.PositionNow;
        ProgressScale.ScaleX = duration.TotalSeconds > 0.5
            ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1)
            : 0;

        PositionText.Text = FormatTime(position);
        DurationText.Text = FormatTime(duration);
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void UpdateEq()
    {
        var playing = _music.IsPlaying && !_hidden && _mode != IslandMode.Drop;
        PillEq.Opacity = playing ? 1 : 0;
        ExpandedEq.Opacity = playing ? 1 : 0;

        if (playing)
            _eq.Start();
        else
            _eq.Stop();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await _music.PreviousAsync();

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _music.TogglePlayPauseAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await _music.NextAsync();

    // ───────────────────────────── погода, время, батарея ─────────────────────────────

    private void OnWeatherUpdated() => UpdateWeatherUi();

    private void UpdateWeatherUi()
    {
        var weather = _weather.Current;
        if (weather == null)
        {
            WeatherIcon.Text = "🌡️";
            WeatherTemp.Text = "--°";
            WeatherRange.Text = "";
            WeatherCity.Text = "погода";
            PillWeatherIcon.Text = "";
            PillWeatherTemp.Text = "";
            return;
        }

        var (glyph, description) = WeatherService.Describe(weather.Code, weather.IsDay);
        var temp = Math.Round(weather.Temperature);
        var min = Math.Round(weather.Min);
        var max = Math.Round(weather.Max);

        WeatherIcon.Text = glyph;
        WeatherIcon.ToolTip = $"{description}, ощущается {Math.Round(weather.FeelsLike)}°, " +
                              $"ветер {Math.Round(weather.Wind)} м/с, обновлено {weather.UpdatedAt:HH:mm}";
        WeatherTemp.Text = $"{temp:0}°";
        WeatherRange.Text = $"{min:0}°/{max:0}°";
        WeatherCity.Text = string.IsNullOrWhiteSpace(weather.City) ? "погода" : weather.City;

        PillWeatherIcon.Text = glyph;
        PillWeatherTemp.Text = $"{temp:0}°";
    }

    private void TickClock()
    {
        var now = DateTime.Now;
        var time = now.ToString("HH:mm", Ru);
        if (PillClock.Text != time)
        {
            PillClock.Text = time;
            ClockTime.Text = time;
        }

        var date = now.ToString("ddd, d MMM", Ru);
        if (ClockDate.Text != date) ClockDate.Text = date;

        UpdateProgress();

        if (++_batteryTick % 30 == 0) UpdateBattery();
        if (_music.IsPlaying) UpdateEq();
    }

    private static readonly Brush BatteryNormal = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
    private static readonly Brush BatteryCharging = new SolidColorBrush(Color.FromArgb(200, 127, 224, 255));
    private static readonly Brush BatteryLow = new SolidColorBrush(Color.FromArgb(235, 255, 138, 120));
    private static readonly Brush BatteryUnknown = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));

    private void UpdateBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status) || status.BatteryLifePercent == 255)
        {
            BatteryShell.Visibility = Visibility.Collapsed;
            PowerGlyph.Visibility = Visibility.Visible;
            BatteryText.Text = "сеть";
            return;
        }

        BatteryShell.Visibility = Visibility.Visible;
        PowerGlyph.Visibility = Visibility.Collapsed;
        var percent = status.BatteryLifePercent;
        BatteryText.Text = $"{percent}%";
        BatteryFill.Width = Math.Max(1, 15.0 * percent / 100.0);
        BatteryFill.Background = status.ACLineStatus == 1
            ? BatteryCharging
            : percent <= 20
                ? BatteryLow
                : BatteryNormal;
    }

    // ───────────────────────────── полный экран ─────────────────────────────

    private void OnFullscreenChanged(bool isFullscreen)
    {
        if (!_settings.HideInFullscreen || _trayHidden) return;

        if (isFullscreen)
        {
            _pinned = false;
            _collapseTimer.Stop();
            _peekTimer.Stop();
            SlideOut();
        }
        else
        {
            SlideIn();
        }
    }

    private void SlideOut()
    {
        if (_hidden) return;

        _hidden = true;
        _music.Suspend();
        _clockTimer.Stop();
        _topmostTimer.Stop();
        _pinned = false;

        var height = Island.ActualHeight > 1 ? Island.ActualHeight : ExpandedHeight;
        var distance = -(height + 90);

        UpdateEq();
        FadeTo(Island, 0, 240);
        AnimateTo(IslandSlide, TranslateTransform.YProperty, IslandSlide.Y, distance, 340,
            new CubicEase { EasingMode = EasingMode.EaseIn }, 0, () =>
            {
                if (!_hidden) return;
                Root.Visibility = Visibility.Collapsed;
                Visibility = Visibility.Hidden;
                NativeMethods.TrimWorkingSet();
            });
    }

    private void SlideIn()
    {
        if (!_hidden) return;

        _hidden = false;
        _trayHidden = false;

        Visibility = Visibility.Visible;
        Root.Visibility = Visibility.Visible;

        SetMode(IslandMode.Compact, animate: false);

        Island.BeginAnimation(OpacityProperty, null);
        Island.Opacity = 0;

        var start = -(Island.ActualHeight + 90);
        IslandSlide.BeginAnimation(TranslateTransform.YProperty, null);
        IslandSlide.Y = start;
        AnimateTo(IslandSlide, TranslateTransform.YProperty, start, 0, 560,
            new CubicEase { EasingMode = EasingMode.EaseOut });
        FadeTo(Island, 1, 340, 40);

        _music.Resume();
        _clockTimer.Start();
        _topmostTimer.Start();

        UpdateMusicUi();
        UpdateWeatherUi();
        UpdateBattery();
        TickClock();
        ReassertTopmost();

        UpdateEq();
        _ = _weather.RefreshAsync();
    }
}
