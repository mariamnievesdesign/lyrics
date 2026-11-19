using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace Letras;

public sealed partial class MainWindow : Window
{
    const double DimOpacity = 0.5;
    const double DimScale = 0.93;
    static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(150); // Muestra cada línea un pelín antes, se siente más natural.

    // Niveles de transparencia: (opacidad del tinte, opacidad de luminosidad). De más transparente a más oscuro.
    static readonly (float Tint, float Luminosity)[] GlassLevels =
    {
        (0.00f, 0.10f),
        (0.15f, 0.35f),
        (0.35f, 0.60f),
        (0.60f, 0.85f),
    };

    readonly MediaWatcher _media = new();
    readonly LyricsService _lyrics = new();
    readonly Settings _settings = Settings.Load();
    readonly DispatcherQueueTimer _timer;
    readonly IntPtr _hwnd;

    DesktopAcrylicController? _acrylic;
    SystemBackdropConfiguration? _backdropConfig;

    string? _trackKey;
    List<LrcLine> _lines = new();
    bool _synced;
    int _current = -2;
    bool _busy;
    int _ticks;
    CancellationTokenSource? _lookup;
    Storyboard? _scrollAnim;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        SetupWindow();
        SetupAcrylic();
        UpdateOffsetText();

        Root.PointerPressed += Root_PointerPressed;
        Root.PointerEntered += (_, _) => Animate(ControlsPanel, "Opacity", 1, 200);
        Root.PointerExited += (_, _) => Animate(ControlsPanel, "Opacity", 0, 200);
        Viewport.SizeChanged += (_, _) =>
        {
            LinesPanel.Width = Viewport.ActualWidth;
            Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight) };
            ScrollToCurrent(animate: false);
        };
        Viewport.PointerWheelChanged += Viewport_PointerWheelChanged;
        Closed += (_, _) =>
        {
            _settings.Save();
            _acrylic?.Dispose();
            _acrylic = null;
        };

        ShowStatus("Esperando música… ♪");

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(120);
        _timer.Tick += (_, _) => Tick();
        _ = StartAsync();
    }

    async Task StartAsync()
    {
        await _media.StartAsync();
        _timer.Start();
    }

    // ---------------- Ventana ----------------

    void SetupWindow()
    {
        var appWindow = AppWindow;
        appWindow.Title = "Letras";

        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = true;
            p.SetBorderAndTitleBar(true, false); // Borde para redimensionar, sin barra de título.
            p.IsAlwaysOnTop = true; // Después de quitar la barra, que puede reiniciar este ajuste.
        }

        // Esquinas redondeadas (Windows 11).
        var corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int));

        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var w = Math.Max(_settings.W ?? 0, (int)(260 * scale));
        var h = Math.Max(_settings.H ?? 0, (int)(150 * scale));
        if (_settings.W == null) { w = (int)(420 * scale); h = (int)(260 * scale); }

        int x, y;
        if (_settings.X is int sx && _settings.Y is int sy &&
            DisplayArea.GetFromPoint(new PointInt32(sx + 40, sy + 40), DisplayAreaFallback.None) != null)
        {
            x = sx;
            y = sy;
        }
        else
        {
            // Primera vez: esquina inferior derecha, sobre la barra de tareas.
            var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            x = work.X + work.Width - w - (int)(24 * scale);
            y = work.Y + work.Height - h - (int)(24 * scale);
        }
        appWindow.MoveAndResize(new RectInt32(x, y, w, h));

        appWindow.Changed += (s, e) =>
        {
            if (!e.DidPositionChange && !e.DidSizeChange) return;
            _settings.X = s.Position.X;
            _settings.Y = s.Position.Y;
            _settings.W = s.Size.Width;
            _settings.H = s.Size.Height;
        };
    }

    // Arrastrar la ventana desde cualquier parte (los botones no lo activan porque manejan su propio clic).
    void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
        ReleaseCapture();
        SendMessage(_hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    void KeepOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    // ---------------- Vidrio acrílico ----------------

    void SetupAcrylic()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            Root.Background = new SolidColorBrush(ColorHelper.FromArgb(0xF2, 0x18, 0x16, 0x1E));
            return;
        }

        // IsInputActive = true siempre: así Windows NO cambia el vidrio por un color sólido
        // cuando la ventana pierde el foco (que es casi siempre, porque estás usando otra app).
        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark,
        };

        _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        ApplyGlassLevel();
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);

        // Por si Windows intenta reactivar el estado "inactivo".
        Activated += (_, _) => { if (_backdropConfig != null) _backdropConfig.IsInputActive = true; };
    }

    void ApplyGlassLevel()
    {
        if (_acrylic == null) return;
        var (tint, lum) = GlassLevels[Math.Clamp(_settings.GlassLevel, 0, GlassLevels.Length - 1)];
        _acrylic.TintColor = ColorHelper.FromArgb(0xFF, 0x18, 0x16, 0x1E);
        _acrylic.TintOpacity = tint;
        _acrylic.LuminosityOpacity = lum;
        _acrylic.FallbackColor = ColorHelper.FromArgb(0xF2, 0x18, 0x16, 0x1E);
    }

    // ---------------- Ciclo principal ----------------

    async void Tick()
    {
        // Cada ~1 s se vuelve a poner encima de todo (otras apps "siempre encima" pueden taparla).
        if (_ticks++ % 8 == 0) KeepOnTop();

        if (_busy) return;
        _busy = true;
        try
        {
            await _media.UpdateAsync();
            var track = _media.Track;

            if (track == null)
            {
                if (_trackKey != null)
                {
                    _trackKey = null;
                    _lookup?.Cancel();
                    ClearLines();
                    ShowTrack(null);
                    ShowStatus("Esperando música… ♪");
                }
                return;
            }

            if (track.Key != _trackKey)
            {
                _trackKey = track.Key;
                ShowTrack(track);
                _ = LoadLyricsAsync(track);
            }

            UpdateHighlight();
        }
        catch { /* Un fallo puntual al leer el reproductor no debe cerrar la app. */ }
        finally { _busy = false; }
    }

    async Task LoadLyricsAsync(TrackInfo track)
    {
        _lookup?.Cancel();
        var cts = _lookup = new CancellationTokenSource();

        ClearLines();
        ShowStatus("Buscando letra…");

        // Espera un momento para que el reproductor reporte la duración (ayuda a elegir la versión correcta).
        try { await Task.Delay(400, cts.Token); } catch (OperationCanceledException) { return; }

        LyricsResult result;
        try { result = await _lyrics.FindAsync(track, _media.Duration, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch
        {
            if (!cts.IsCancellationRequested) ShowStatus("No pude conectarme para buscar la letra");
            return;
        }

        if (cts.IsCancellationRequested || track.Key != _trackKey) return;

        if (result.Synced is { Count: > 0 })
        {
            BuildLines(result.Synced, synced: true);
            HideStatus();
        }
        else if (!string.IsNullOrWhiteSpace(result.Plain))
        {
            var plain = result.Plain.Split('\n').Select(l => new LrcLine(TimeSpan.Zero, l.Trim())).ToList();
            BuildLines(plain, synced: false);
            HideStatus();
        }
        else if (result.Instrumental) ShowStatus("♪ Instrumental ♪");
        else ShowStatus("No encontré la letra de esta canción");
    }

    // ---------------- Letra ----------------

    void BuildLines(List<LrcLine> lines, bool synced)
    {
        ClearLines();
        _lines = lines;
        _synced = synced;

        foreach (var line in lines)
        {
            var scale = synced ? DimScale : 1;
            var tb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = _settings.FontSize,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 5),
                Opacity = synced ? DimOpacity : 0.9,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale },
            };
            LinesPanel.Children.Add(tb);
        }

        if (!synced)
        {
            ResetScroll(0);
            return;
        }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ScrollToCurrent(animate: false));
    }

    void ClearLines()
    {
        LinesPanel.Children.Clear();
        _lines = new();
        _synced = false;
        _current = -2;
        ResetScroll(0);
    }

    void UpdateHighlight()
    {
        if (!_synced || _lines.Count == 0) return;

        var pos = _media.Position + TimeSpan.FromSeconds(_settings.OffsetSeconds) + Lead;
        var idx = -1;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Time <= pos) idx = i;
            else break;
        }

        if (idx == _current) return;
        SetActive(_current, false);
        SetActive(idx, true);
        _current = idx;
        ScrollToCurrent(animate: true);
    }

    void SetActive(int index, bool active)
    {
        if (index < 0 || index >= LinesPanel.Children.Count) return;
        var tb = (TextBlock)LinesPanel.Children[index];
        Animate(tb, "Opacity", active ? 1 : DimOpacity, 350);
        if (tb.RenderTransform is ScaleTransform st)
        {
            var s = active ? 1 : DimScale;
            Animate(st, "ScaleX", s, 350);
            Animate(st, "ScaleY", s, 350);
        }
    }

    void ScrollToCurrent(bool animate)
    {
        if (!_synced || LinesPanel.Children.Count == 0) return;
        var i = Math.Clamp(_current, 0, LinesPanel.Children.Count - 1);
        var tb = (FrameworkElement)LinesPanel.Children[i];

        LinesPanel.UpdateLayout();
        var center = tb.TransformToVisual(LinesPanel).TransformPoint(new Point(0, tb.ActualHeight / 2)).Y;
        var target = Viewport.ActualHeight * 0.42 - center;

        if (!animate) { ResetScroll(target); return; }
        _scrollAnim = Animate(Scroll, "Y", target, 450);
    }

    void ResetScroll(double y)
    {
        _scrollAnim?.Stop();
        _scrollAnim = null;
        Scroll.Y = y;
    }

    // Con letra sin sincronizar, la rueda del mouse permite recorrerla.
    void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_synced || LinesPanel.Children.Count == 0) return;
        var delta = e.GetCurrentPoint(Viewport).Properties.MouseWheelDelta;
        var min = Math.Min(0, Viewport.ActualHeight - LinesPanel.ActualHeight - 20);
        ResetScroll(Math.Clamp(Scroll.Y + delta * 0.4, min, 0));
    }

    static Storyboard Animate(DependencyObject target, string property, double to, int ms)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
        return sb;
    }

    // ---------------- Encabezado y estado ----------------

    void ShowTrack(TrackInfo? track)
    {
        if (track == null) { TrackInfoPanel.Visibility = Visibility.Collapsed; return; }
        TitleText.Text = track.Title;
        ArtistText.Text = track.Artist;
        ArtistText.Visibility = string.IsNullOrWhiteSpace(track.Artist) ? Visibility.Collapsed : Visibility.Visible;
        TrackInfoPanel.Visibility = Visibility.Visible;
    }

    void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    void HideStatus() => StatusText.Visibility = Visibility.Collapsed;

    // ---------------- Controles ----------------

    void Earlier_Click(object sender, RoutedEventArgs e) => ChangeOffset(-0.5);
    void Later_Click(object sender, RoutedEventArgs e) => ChangeOffset(+0.5);

    void ChangeOffset(double delta)
    {
        _settings.OffsetSeconds = Math.Round(_settings.OffsetSeconds + delta, 1);
        UpdateOffsetText();
        UpdateHighlight();
    }

    void UpdateOffsetText() =>
        OffsetText.Text = (_settings.OffsetSeconds > 0 ? "+" : "") +
                          _settings.OffsetSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    void Smaller_Click(object sender, RoutedEventArgs e) => ChangeFont(-2);
    void Bigger_Click(object sender, RoutedEventArgs e) => ChangeFont(+2);

    void ChangeFont(double delta)
    {
        _settings.FontSize = Math.Clamp(_settings.FontSize + delta, 14, 48);
        foreach (var child in LinesPanel.Children)
            if (child is TextBlock tb) tb.FontSize = _settings.FontSize;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ScrollToCurrent(animate: false));
    }

    void Glass_Click(object sender, RoutedEventArgs e)
    {
        _settings.GlassLevel = (_settings.GlassLevel + 1) % GlassLevels.Length;
        ApplyGlassLevel();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- Win32 ----------------

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
