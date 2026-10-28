using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LyricPop;

/// <summary>Animasi scroll halus: ScrollViewer tidak punya DependencyProperty offset, jadi dibuat sendiri.</summary>
public static class ScrollAnim
{
    public static readonly DependencyProperty VOffsetProperty = DependencyProperty.RegisterAttached(
        "VOffset", typeof(double), typeof(ScrollAnim),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    public static void SetVOffset(DependencyObject o, double v) => o.SetValue(VOffsetProperty, v);
    public static double GetVOffset(DependencyObject o) => (double)o.GetValue(VOffsetProperty);
}

/// <summary>
/// Widget lirik mengambang. Tiga mode: ringkas (1 baris), diperluas (lirik lengkap + progress), mini.
/// Sumber lagu: Windows media session (MediaWatcher). Lirik: LRCLIB (LyricsClient).
/// </summary>
public partial class MainWindow : Window
{
    private enum Mode { Compact, Expanded, Mini }
    private enum LStatus { Idle, Loading, Synced, Plain, Instrumental, None, Error }

    private const double Lead = 0.2; // tampilkan baris sedikit lebih awal (detik)
    private static readonly TimeSpan IdleHide = TimeSpan.FromSeconds(30);

    private readonly AppSettings _s = AppSettings.Load();
    private readonly MediaWatcher _watcher = new();
    private readonly LyricsClient _client = new();
    private readonly DispatcherTimer _timer;

    private Mode _mode = Mode.Compact, _prevMode = Mode.Compact;
    private LStatus _status = LStatus.Idle;
    private List<LrcLine> _lines = new();
    private List<string> _plain = new();
    private LyricData? _data;
    private readonly List<TextBlock> _lineViews = new();
    private int _activeIdx = -2;

    private string? _trackKey, _dismissedKey;
    private Track? _track;
    private bool _everPlayed, _shown;
    private DateTime? _pausedSince;
    private Hue? _palette;
    private int _lastThumbGen = -1, _lyricToken;
    private DateTime _userScrollUntil = DateTime.MinValue;
    private bool? _lastPlaying;

    private Brush _fg = Brushes.White, _dim = Brushes.Gray, _faint = Brushes.Gray, _accent = Brushes.HotPink, _onAccent = Brushes.Black;

    private Dictionary<string, MenuItem> _themeItems = new();
    private Dictionary<int, MenuItem> _opacityItems = new();
    private MenuItem? _onTopItem, _autoItem, _offsetResetItem;

    private const string PathPlay = "M8,5 v14 l11,-7 z";
    private const string PathPause = "M6,5 h4 v14 H6 z M14,5 h4 v14 h-4 z";
    private const string PathMin = "M5,11 h14 v2 H5 z";
    private const string PathRestore = "M5,5 h6 v2 H7 v4 H5 z M13,5 h6 v6 h-2 V7 h-4 z M5,13 h2 v4 h4 v2 H5 z M17,13 h2 v6 h-6 v-2 h4 z";

    public MainWindow()
    {
        InitializeComponent();
        Topmost = _s.AlwaysOnTop;
        Wire();
        BuildMenu();
        ApplyStyle();
        ApplyMode();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => { try { Frame(); } catch (Exception) { /* jaga agar timer tidak berhenti */ } };
    }

    public async Task StartAsync()
    {
        try { await _watcher.InitAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("Tidak bisa membaca media session Windows.\nButuh Windows 10 (1809) atau lebih baru.\n\n" + ex.Message,
                "Lyric Pop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _timer.Start();
    }

    public void Cleanup() { _timer.Stop(); }

    /// <summary>Dipanggil dari tray: tampilkan lagi widget yang ditutup / disembunyikan.</summary>
    public void ShowAgain()
    {
        _dismissedKey = null;
        _everPlayed = true;
        if (!_watcher.Playing) _pausedSince = DateTime.UtcNow;
    }

    public static void ShowAbout() =>
        MessageBox.Show("Lyric Pop 1.0\nWidget lirik tersinkron untuk Windows.\n\nDibuat oleh arlnxs\nLirik dari LRCLIB (lrclib.net)",
            "Tentang Lyric Pop", MessageBoxButton.OK, MessageBoxImage.Information);

    // ================================================================ input
    private void Wire()
    {
        BtnPlay.Click += async (_, _) => await _watcher.TogglePlayPauseAsync();
        BtnPrev.Click += async (_, _) => await _watcher.PrevAsync();
        BtnNext.Click += async (_, _) => await _watcher.NextAsync();
        BtnMin.Click += (_, _) =>
        {
            if (_mode == Mode.Mini) SetMode(_prevMode == Mode.Mini ? Mode.Compact : _prevMode);
            else SetMode(Mode.Mini);
        };
        BtnClose.Click += (_, _) => { _dismissedKey = _trackKey; };

        NowText.MouseLeftButtonUp += (_, _) => SetMode(Mode.Expanded);
        NowBtn.MouseLeftButtonUp += (_, _) => OpenSearch();
        ManualLink.MouseLeftButtonUp += (_, _) => OpenSearch();
        Bar.MouseLeftButtonDown += OnBarDown;
        Scroll.PreviewMouseWheel += (_, _) => _userScrollUntil = DateTime.UtcNow.AddSeconds(3.5);
        Head.MouseLeftButtonDown += OnHeadDown;
    }

    // Seret header = pindahkan window; klik (tanpa geser) = ringkas <-> diperluas
    private void OnHeadDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        double l0 = Left, t0 = Top;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        if (Math.Abs(Left - l0) < 3 && Math.Abs(Top - t0) < 3)
        {
            SetMode(_mode == Mode.Compact ? Mode.Expanded : Mode.Compact);
        }
        else
        {
            _s.Left = Left; _s.Top = Top; _s.Save();
        }
    }

    private async void OnBarDown(object sender, MouseButtonEventArgs e)
    {
        var dur = _watcher.Duration;
        if (dur <= TimeSpan.Zero || !_watcher.CanSeek || Bar.ActualWidth <= 0) return;
        double r = Math.Clamp(e.GetPosition(Bar).X / Bar.ActualWidth, 0, 1);
        await _watcher.SeekAsync(TimeSpan.FromSeconds(dur.TotalSeconds * r));
    }

    // ============================================================= menu klik-kanan
    private void BuildMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string header, Action act)
        {
            var i = new MenuItem { Header = header };
            i.Click += (_, _) => act();
            return i;
        }

        var theme = new MenuItem { Header = "Tema" };
        foreach (var (key, name) in new[] { ("dark", "Gelap"), ("light", "Terang"), ("artwork", "Warna artwork") })
        {
            var it = Item(name, () => { _s.Theme = key; _s.Save(); ApplyStyle(); });
            _themeItems[key] = it;
            theme.Items.Add(it);
        }
        m.Items.Add(theme);

        var font = new MenuItem { Header = "Ukuran font lirik" };
        font.Items.Add(Item("Lebih besar (A+)", () => SetFont(_s.FontSize + 2)));
        font.Items.Add(Item("Lebih kecil (A\u2212)", () => SetFont(_s.FontSize - 2)));
        m.Items.Add(font);

        var op = new MenuItem { Header = "Transparansi latar" };
        foreach (var v in new[] { 100, 90, 80, 70, 60, 50 })
        {
            int val = v;
            var it = Item(val + "%", () => { _s.Opacity = val; _s.Save(); ApplyStyle(); });
            _opacityItems[val] = it;
            op.Items.Add(it);
        }
        m.Items.Add(op);

        var sync = new MenuItem { Header = "Sinkron lirik" };
        sync.Items.Add(Item("Lirik lebih awal (+0,5 dtk)", () => SetOffset(_s.OffsetSec + 0.5)));
        sync.Items.Add(Item("Lirik lebih lambat (\u22120,5 dtk)", () => SetOffset(_s.OffsetSec - 0.5)));
        _offsetResetItem = Item("Reset", () => SetOffset(0));
        sync.Items.Add(_offsetResetItem);
        m.Items.Add(sync);

        m.Items.Add(new Separator());
        m.Items.Add(Item("Cari lirik manual\u2026", OpenSearch));
        _onTopItem = Item("Selalu di atas", () => { _s.AlwaysOnTop = !_s.AlwaysOnTop; Topmost = _s.AlwaysOnTop; _s.Save(); });
        m.Items.Add(_onTopItem);
        _autoItem = Item("Mulai bersama Windows", () => Autostart.Set(!Autostart.IsEnabled()));
        m.Items.Add(_autoItem);
        m.Items.Add(Item("Reset posisi", () =>
        {
            _s.Left = null; _s.Top = null; _s.Save();
            PositionWindow(true);
        }));
        m.Items.Add(Item("Hapus cache lirik", () =>
        {
            int n = _client.ClearCache();
            MessageBox.Show($"Cache dihapus ({n} lagu).", "Lyric Pop", MessageBoxButton.OK, MessageBoxImage.Information);
        }));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Tentang \u2014 dibuat oleh arlnxs", ShowAbout));
        m.Items.Add(Item("Keluar", () => Application.Current.Shutdown()));

        m.Opened += (_, _) =>
        {
            foreach (var kv in _themeItems) kv.Value.IsChecked = _s.Theme == kv.Key;
            foreach (var kv in _opacityItems) kv.Value.IsChecked = _s.Opacity == kv.Key;
            if (_onTopItem != null) _onTopItem.IsChecked = _s.AlwaysOnTop;
            if (_autoItem != null) _autoItem.IsChecked = Autostart.IsEnabled();
            if (_offsetResetItem != null) _offsetResetItem.Header = $"Reset (sekarang {_s.OffsetSec:+0.0;-0.0;0} dtk)";
        };
        Root.ContextMenu = m;
    }

    private void SetFont(int v) { _s.FontSize = Math.Clamp(v, 12, 36); _s.Save(); ApplyStyle(); }
    private void SetOffset(double v) { _s.OffsetSec = Math.Round(Math.Clamp(v, -10, 10), 1); _s.Save(); }

    // ============================================================== tampil / sembunyi
    private void PositionWindow(bool forceDefault)
    {
        var wa = SystemParameters.WorkArea;
        if (!forceDefault && _s.Left is double l && _s.Top is double t) { Left = l; Top = t; }
        else
        {
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top;
        }
        Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);
    }

    private void ClampToScreen()
    {
        var wa = SystemParameters.VirtualScreenLeft;
        double sl = SystemParameters.VirtualScreenLeft, st = SystemParameters.VirtualScreenTop;
        double sw = SystemParameters.VirtualScreenWidth, sh = SystemParameters.VirtualScreenHeight;
        double h = ActualHeight > 0 ? ActualHeight : 120;
        Left = Math.Clamp(Left, sl - 10, Math.Max(sl - 10, sl + sw - Width + 10));
        Top = Math.Clamp(Top, st - 10, Math.Max(st - 10, st + sh - h + 10));
        _ = wa;
    }

    private void ShowWidget()
    {
        _shown = true;
        if (!IsVisible) { PositionWindow(false); Show(); }
        Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
        RootShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void HideWidget()
    {
        _shown = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => { if (!_shown) Hide(); };
        Root.BeginAnimation(UIElement.OpacityProperty, fade);
        RootShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-18, TimeSpan.FromMilliseconds(250)));
    }

    /// <summary>Animasi ganti lagu: keluar (fade) -> ganti isi -> masuk.</summary>
    private void Swap(Action apply)
    {
        var o = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        o.Completed += (_, _) =>
        {
            apply();
            Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        };
        Root.BeginAnimation(UIElement.OpacityProperty, o);
    }

    private bool ShouldShow()
    {
        if (!_watcher.HasMeta || _track == null) return false;
        if (_dismissedKey != null && _dismissedKey == _trackKey) return false;
        if (_watcher.Playing) return true;
        return _everPlayed && _pausedSince != null && DateTime.UtcNow - _pausedSince.Value < IdleHide;
    }

    // ================================================================== loop utama
    private void Frame()
    {
        _watcher.Poll();
        Reconcile();

        bool show = ShouldShow();
        if (show && !_shown) ShowWidget();
        else if (!show && _shown) HideWidget();
        if (!_shown) return;

        var pos = _watcher.Position();
        UpdateProgress(pos);
        if (_status == LStatus.Synced)
        {
            int idx = Txt.IndexAt(_lines, pos.TotalSeconds + _s.OffsetSec + Lead);
            if (idx != _activeIdx) { _activeIdx = idx; Highlight(idx); }
        }
    }

    private void Reconcile()
    {
        if (!_watcher.HasMeta) { _pausedSince = null; return; }

        var key = _watcher.Title + "\u241f" + _watcher.Artist + "\u241f" + _watcher.Album;
        if (key != _trackKey) ChangeTrack(key);
        else if (_watcher.ThumbGen != _lastThumbGen) ApplyArt();

        if (_watcher.Playing) { _everPlayed = true; _pausedSince = null; }
        else _pausedSince ??= DateTime.UtcNow;

        if (_lastPlaying != _watcher.Playing)
        {
            _lastPlaying = _watcher.Playing;
            PlayIcon.Data = Geometry.Parse(_watcher.Playing ? PathPause : PathPlay);
        }
        BtnPrev.IsEnabled = _watcher.CanPrev;
        BtnNext.IsEnabled = _watcher.CanNext;
    }

    private void ChangeTrack(string key)
    {
        _trackKey = key;
        var t = Txt.ParseTrack(_watcher.Title, _watcher.Artist, _watcher.Album);
        _track = t;
        if (_dismissedKey != null && _dismissedKey != key) _dismissedKey = null;
        _status = LStatus.Loading;
        _lines = new(); _plain = new(); _data = null; _activeIdx = -2;

        Action apply = () =>
        {
            TitleText.Text = t.Title;
            ArtistText.Text = t.Artist;
            RenderLyrics();
            ApplyArt();
        };
        if (_shown) Swap(apply); else apply();

        int token = ++_lyricToken;
        _ = FetchLyricsAsync(token, t);
    }

    private async Task FetchLyricsAsync(int token, Track t)
    {
        await Task.Delay(500); // debounce: metadata sering berubah beruntun
        if (token != _lyricToken) return;
        int dur = (int)_watcher.Duration.TotalSeconds;
        var (ok, d) = await _client.GetAsync(t, dur);
        if (token != _lyricToken) return;
        ApplyResult(ok, d);
    }

    private void ApplyResult(bool ok, LyricData? d)
    {
        _activeIdx = -2; _lines = new(); _plain = new(); _data = null;
        if (!ok) _status = LStatus.Error;
        else if (d == null) _status = LStatus.None;
        else
        {
            var parsed = d.Synced.Length > 0 ? Txt.ParseLrc(d.Synced) : new List<LrcLine>();
            if (parsed.Count > 0) { _status = LStatus.Synced; _lines = parsed; _data = d; }
            else if (d.Plain.Length > 0) { _status = LStatus.Plain; _plain = Txt.SplitLines(d.Plain).ToList(); _data = d; }
            else if (d.IsInstrumental) { _status = LStatus.Instrumental; _data = d; }
            else _status = LStatus.None;
        }
        RenderLyrics();
    }

    private string SrcLabel()
    {
        string s = _status switch
        {
            LStatus.Synced => "LRCLIB \u00B7 tersinkron \u00B7 ",
            LStatus.Plain => "LRCLIB \u00B7 teks biasa \u00B7 ",
            _ => ""
        };
        return s + "dibuat oleh arlnxs";
    }

    private void OpenSearch()
    {
        var t = _track;
        string q = t == null ? "" : (t.Title + " " + (t.Primary.Length > 0 ? t.Primary : t.Artist)).Trim();
        var w = new SearchWindow(_client, q, item =>
        {
            if (_track == null) return;
            _lyricToken++; // batalkan pencarian otomatis yang masih berjalan
            _client.Save(_track, item);
            ApplyResult(true, item);
        });
        w.Show();
        w.Activate();
    }

    // ================================================================== artwork
    private void ApplyArt()
    {
        _lastThumbGen = _watcher.ThumbGen;
        var bytes = _watcher.Thumb;
        _palette = null;
        if (bytes == null || bytes.Length == 0)
        {
            ArtBox.Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0x6A, 0xA2), Color.FromRgb(0x7C, 0x5C, 0xFF), 45);
            ArtNote.Visibility = Visibility.Visible;
        }
        else
        {
            try
            {
                var bmp = new BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 192;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                ArtBox.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                ArtNote.Visibility = Visibility.Collapsed;
                _palette = Txt.DominantHue(SamplePixels(bmp));
            }
            catch (Exception) { _palette = null; }
        }
        ApplyStyle();
    }

    private static byte[] SamplePixels(BitmapSource bmp)
    {
        var scaled = new TransformedBitmap(bmp, new ScaleTransform(32.0 / bmp.PixelWidth, 32.0 / bmp.PixelHeight));
        var fmt = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        int w = fmt.PixelWidth, h = fmt.PixelHeight;
        var px = new byte[w * h * 4];
        fmt.CopyPixels(px, w * 4, 0);
        return px;
    }

    // ================================================================== tema
    private void ApplyStyle()
    {
        bool light = _s.Theme == "light";
        var h = _palette ?? new Hue(338, 0.85);
        var fgC = light ? Color.FromRgb(0x17, 0x17, 0x1C) : Colors.White;
        _fg = Pal.Brush(fgC);
        _dim = Pal.Brush(Pal.WithAlpha(fgC, 0.64));
        _faint = Pal.Brush(Pal.WithAlpha(fgC, 0.40));

        double sat = h.S < 0.12 ? h.S : Math.Clamp(h.S, 0.55, 0.95);
        var accentC = Pal.Hsl(h.H, sat, light ? 0.44 : 0.64);
        var onAccentC = light ? Colors.White : Color.FromRgb(0x10, 0x10, 0x14);
        _accent = Pal.Brush(accentC);
        _onAccent = Pal.Brush(onAccentC);

        Color b1, b2;
        double s2 = Math.Clamp(h.S * 0.6, 0, 0.55);
        switch (_s.Theme)
        {
            case "light": b1 = Colors.White; b2 = Color.FromRgb(0xF3, 0xF4, 0xFA); break;
            case "dark": b1 = Color.FromRgb(0x12, 0x12, 0x18); b2 = b1; break;
            default: b1 = Pal.Hsl(h.H, s2, 0.13); b2 = Pal.Hsl(h.H + 24, s2, 0.23); break;
        }
        double a = Math.Clamp(_s.Opacity, 30, 100) / 100.0;
        Root.Background = new LinearGradientBrush(Pal.WithAlpha(b1, a), Pal.WithAlpha(b2, a), 45);
        Root.BorderBrush = Pal.Brush(Pal.WithAlpha(fgC, 0.14));

        TitleText.Foreground = _fg;
        ArtistText.Foreground = _dim;
        NowText.Foreground = _fg;
        NowText.FontSize = _s.FontSize;
        NowText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        NowText.LineHeight = _s.FontSize * 1.3;
        NowText.MaxHeight = _s.FontSize * 1.3 * 2;
        CurText.Foreground = _dim; DurText.Foreground = _dim;
        StateText.Foreground = _dim;
        SrcText.Foreground = _faint;
        ManualLink.Foreground = _dim;
        BarTrack.Background = Pal.Brush(Pal.WithAlpha(fgC, 0.2));
        BarFill.Background = _accent;

        foreach (var b in new[] { BtnPrev, BtnNext, BtnMin, BtnClose }) b.Foreground = _fg;
        BtnPlay.Foreground = _onAccent;
        BtnPlay.Background = _accent;
        NowBtn.Background = _accent;
        NowBtnText.Foreground = _onAccent;

        foreach (var tb in Lines.Children.OfType<TextBlock>())
        {
            tb.Foreground = _fg;
            tb.FontSize = _s.FontSize;
        }
    }

    // ================================================================== mode
    private void SetMode(Mode m)
    {
        if (m == _mode) return;
        double oldW = Width;
        if (m == Mode.Mini) _prevMode = _mode;
        _mode = m;
        ApplyMode();
        Left += (oldW - Width) / 2; // tetap berpusat saat lebar berubah
        Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);
        if (m == Mode.Expanded) Dispatcher.BeginInvoke(new Action(() => ScrollToActive(true)), DispatcherPriority.Loaded);
    }

    private void ApplyMode()
    {
        bool mini = _mode == Mode.Mini;
        Width = mini ? 298 : 440; // kartu 270 / 412 + margin bayangan 2x14
        Body.Visibility = _mode == Mode.Expanded ? Visibility.Visible : Visibility.Collapsed;
        NowText.Visibility = _mode == Mode.Compact ? Visibility.Visible : Visibility.Collapsed;
        NowBtn.Visibility = _mode == Mode.Compact && (_status == LStatus.None || _status == LStatus.Error)
            ? Visibility.Visible : Visibility.Collapsed;
        ArtistText.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        BtnPrev.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        double sz = mini ? 32 : 44;
        ArtBox.Width = sz; ArtBox.Height = sz;
        ArtBox.CornerRadius = new CornerRadius(mini ? sz / 2 : 12);
        MinIcon.Data = Geometry.Parse(mini ? PathRestore : PathMin);
        BtnMin.ToolTip = mini ? "Perbesar" : "Perkecil";
    }

    // ================================================================== lirik
    private TextBlock MakeLine(string text, bool plain) => new()
    {
        Text = text,
        FontSize = _s.FontSize,
        FontWeight = plain ? FontWeights.Normal : FontWeights.Bold,
        Foreground = _fg,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 6, 8, 6),
        Opacity = plain ? 0.86 : 0.26,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform(1, 1)
    };

    private void SetNow(string text)
    {
        if (NowText.Text == text) return;
        NowText.Text = text;
        NowText.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        NowShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void RenderLyrics()
    {
        Lines.Children.Clear();
        _lineViews.Clear();
        _activeIdx = -2;
        bool hasList = _status == LStatus.Synced || _status == LStatus.Plain;
        Scroll.Visibility = hasList ? Visibility.Visible : Visibility.Collapsed;
        StateText.Visibility = hasList ? Visibility.Collapsed : Visibility.Visible;
        ManualLink.Visibility = _status == LStatus.Loading || _status == LStatus.Idle ? Visibility.Collapsed : Visibility.Visible;
        ManualLink.Text = _status == LStatus.None || _status == LStatus.Error ? "Cari manual" : "Lirik salah? Cari manual";
        SrcText.Text = SrcLabel();

        switch (_status)
        {
            case LStatus.Synced:
                Lines.Margin = new Thickness(8, 95, 8, 95);
                foreach (var l in _lines)
                {
                    var tb = MakeLine(l.Text.Length > 0 ? l.Text : "\u266A", false);
                    _lineViews.Add(tb);
                    Lines.Children.Add(tb);
                }
                SetNow("\u266A");
                break;
            case LStatus.Plain:
                Lines.Margin = new Thickness(8, 12, 8, 12);
                foreach (var p in _plain) Lines.Children.Add(MakeLine(string.IsNullOrWhiteSpace(p) ? "\u00A0" : p, true));
                SetNow("Lirik tidak tersinkron \u2014 klik untuk membaca");
                break;
            case LStatus.Instrumental:
                StateText.Text = "\u266A Instrumental\nLagu ini tidak memiliki lirik.";
                SetNow("\u266A Instrumental");
                break;
            case LStatus.Error:
                StateText.Text = "Gagal memuat lirik.\nPeriksa koneksi internet.";
                SetNow("Gagal memuat lirik");
                break;
            case LStatus.None:
                StateText.Text = "Lirik tidak ditemukan.\nCoba cari dengan judul atau artis lain.";
                SetNow("Lirik tidak ditemukan");
                break;
            default:
                StateText.Text = "Mencari lirik\u2026";
                SetNow("Mencari lirik\u2026");
                break;
        }
        ApplyMode();
    }

    /// <summary>Sorot baris aktif; baris lain redup, yang bersebelahan sedikit lebih terang.</summary>
    private void Highlight(int idx)
    {
        for (int i = _activeIdx - 1; i <= _activeIdx + 1; i++) StyleLine(i, 0);
        _activeIdx = idx;
        StyleLine(idx, 2); StyleLine(idx - 1, 1); StyleLine(idx + 1, 1);
        SetNow(idx >= 0 && idx < _lines.Count ? (_lines[idx].Text.Length > 0 ? _lines[idx].Text : "\u266A") : "\u266A");
        ScrollToActive(false);
    }

    private void StyleLine(int i, int level)
    {
        if (i < 0 || i >= _lineViews.Count) return;
        var tb = _lineViews[i];
        double o = level == 2 ? 1 : level == 1 ? 0.52 : 0.26;
        double sc = level == 2 ? 1.1 : 1;
        var d = TimeSpan.FromMilliseconds(250);
        tb.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(o, d));
        if (tb.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sc, d));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sc, d));
        }
    }

    private void ScrollToActive(bool instant)
    {
        if (_mode != Mode.Expanded || _status != LStatus.Synced) return;
        if (!instant && DateTime.UtcNow < _userScrollUntil) return;
        if (_activeIdx < 0 || _activeIdx >= _lineViews.Count) return;
        var tb = _lineViews[_activeIdx];
        if (!tb.IsLoaded) return;
        double y = Lines.Margin.Top + tb.TranslatePoint(new Point(0, 0), Lines).Y
                   + tb.ActualHeight / 2 - Scroll.ViewportHeight / 2;
        y = Math.Max(0, y);
        if (instant)
        {
            Scroll.BeginAnimation(ScrollAnim.VOffsetProperty, null);
            Scroll.ScrollToVerticalOffset(y);
        }
        else
        {
            Scroll.BeginAnimation(ScrollAnim.VOffsetProperty, new DoubleAnimation(Scroll.VerticalOffset, y, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private void UpdateProgress(TimeSpan pos)
    {
        if (_mode != Mode.Expanded) return;
        var dur = _watcher.Duration;
        CurText.Text = Txt.Fmt(pos);
        DurText.Text = dur > TimeSpan.Zero ? Txt.Fmt(dur) : "--:--";
        double r = dur > TimeSpan.Zero ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
        BarFill.Width = Bar.ActualWidth * r;
    }
}
