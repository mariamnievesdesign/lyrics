using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Letras;

public partial class MainWindow : Window
{
    const double DimOpacity = 0.45;
    const double DimScale = 0.93;
    static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(150); // Muestra cada línea un pelín antes, se siente más natural.

    readonly MediaWatcher _media = new();
    readonly LyricsService _lyrics = new();
    readonly Settings _settings = Settings.Load();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    string? _trackKey;
    List<LrcLine> _lines = new();
    bool _synced;
    int _current = -2;
    bool _busy;
    CancellationTokenSource? _lookup;

    public MainWindow()
    {
        InitializeComponent();
        ApplySettings();

        SourceInitialized += (_, _) =>
        {
            // Si este Windows no tiene vidrio acrílico, usamos un fondo oscuro sólido para que la letra se lea.
            if (!Glass.Enable(this)) Card.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x18, 0x18, 0x20));
        };
        Loaded += async (_, _) => { await _media.StartAsync(); _timer.Start(); };
        Closing += (_, _) => SaveSettings();

        _timer.Tick += (_, _) => Tick();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        MouseEnter += (_, _) => Fade(ControlsPanel, 1);
        MouseLeave += (_, _) => Fade(ControlsPanel, 0);
        Viewport.SizeChanged += (_, _) => ScrollToCurrent(animate: false);
        Viewport.MouseWheel += Viewport_MouseWheel;

        ShowStatus("Esperando música… ♪");
    }

    // ---------------- Ciclo principal ----------------

    async void Tick()
    {
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
            var tb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                Foreground = Brushes.White,
                FontSize = _settings.FontSize,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 5),
                Opacity = synced ? DimOpacity : 0.9,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(synced ? DimScale : 1, synced ? DimScale : 1),
            };
            LinesPanel.Children.Add(tb);
        }

        if (!synced)
        {
            ResetScroll(0);
            return;
        }
        Dispatcher.InvokeAsync(() => ScrollToCurrent(animate: false), DispatcherPriority.Loaded);
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
        var dur = TimeSpan.FromMilliseconds(350);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        tb.BeginAnimation(OpacityProperty, new DoubleAnimation(active ? 1 : DimOpacity, dur) { EasingFunction = ease });
        if (tb.RenderTransform is ScaleTransform st)
        {
            var scale = active ? 1 : DimScale;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, dur) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, dur) { EasingFunction = ease });
        }
    }

    void ScrollToCurrent(bool animate)
    {
        if (!_synced || LinesPanel.Children.Count == 0) return;
        var i = Math.Clamp(_current, 0, LinesPanel.Children.Count - 1);
        var tb = (FrameworkElement)LinesPanel.Children[i];

        LinesPanel.UpdateLayout();
        var center = tb.TranslatePoint(new Point(0, tb.ActualHeight / 2), LinesPanel).Y;
        var target = Viewport.ActualHeight * 0.42 - center;

        if (!animate) { ResetScroll(target); return; }
        Scroll.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(450))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void ResetScroll(double y)
    {
        Scroll.BeginAnimation(TranslateTransform.YProperty, null);
        Scroll.Y = y;
    }

    // Con letra sin sincronizar, la rueda del mouse permite recorrerla.
    void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_synced || LinesPanel.Children.Count == 0) return;
        var min = Math.Min(0, Viewport.ActualHeight - LinesPanel.ActualHeight - 20);
        ResetScroll(Math.Clamp(Scroll.Y + e.Delta * 0.4, min, 0));
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

    static void Fade(UIElement el, double to) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(200)));

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
                          _settings.OffsetSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";

    void Smaller_Click(object sender, RoutedEventArgs e) => ChangeFont(-2);
    void Bigger_Click(object sender, RoutedEventArgs e) => ChangeFont(+2);

    void ChangeFont(double delta)
    {
        _settings.FontSize = Math.Clamp(_settings.FontSize + delta, 14, 48);
        foreach (TextBlock tb in LinesPanel.Children) tb.FontSize = _settings.FontSize;
        Dispatcher.InvokeAsync(() => ScrollToCurrent(animate: false), DispatcherPriority.Loaded);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- Preferencias ----------------

    void ApplySettings()
    {
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;

        if (_settings.Left is double l && _settings.Top is double t && screen.Contains(new Point(l + 40, t + 40)))
        {
            Left = l;
            Top = t;
        }
        else
        {
            // Primera vez: esquina inferior derecha, sobre la barra de tareas.
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 24;
            Top = work.Bottom - Height - 24;
        }
        UpdateOffsetText();
    }

    void SaveSettings()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = ActualWidth;
        _settings.Height = ActualHeight;
        _settings.Save();
    }
}
