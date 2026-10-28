using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LyricPop;

/// <summary>Dialog pencarian lirik manual (dari menu klik-kanan atau tombol "Cari manual").</summary>
public sealed class SearchWindow : Window
{
    private readonly LyricsClient _client;
    private readonly Action<LyricData> _onPick;
    private readonly TextBox _q = new();
    private readonly StackPanel _results = new();

    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1D));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xB0));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x9D));

    public SearchWindow(LyricsClient client, string query, Action<LyricData> onPick)
    {
        _client = client;
        _onPick = onPick;
        Title = "Cari lirik - Lyric Pop";
        Width = 460; Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = Panel;
        Foreground = Brushes.White;

        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _q.Text = query;
        _q.FontSize = 14;
        _q.Padding = new Thickness(8, 6, 8, 6);
        _q.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2C));
        _q.Foreground = Brushes.White;
        _q.CaretBrush = Brushes.White;
        _q.BorderBrush = Accent;
        _q.KeyDown += (_, e) => { if (e.Key == Key.Enter) RunSearch(); };
        Grid.SetColumn(_q, 0);
        top.Children.Add(_q);

        var go = new Button { Content = "Cari", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
        go.Click += (_, _) => RunSearch();
        Grid.SetColumn(go, 1);
        top.Children.Add(go);
        Grid.SetRow(top, 0);
        grid.Children.Add(top);

        var scroll = new ScrollViewer
        {
            Content = _results,
            Margin = new Thickness(0, 10, 0, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var foot = new TextBlock { Text = "Lyric Pop \u00B7 dibuat oleh arlnxs \u00B7 lirik dari LRCLIB", Foreground = Dim, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetRow(foot, 2);
        grid.Children.Add(foot);

        Content = grid;
        Loaded += (_, _) => { _q.Focus(); _q.SelectAll(); };
    }

    private void Info(string text)
    {
        _results.Children.Clear();
        _results.Children.Add(new TextBlock
        {
            Text = text, Foreground = Dim, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 24, 0, 0)
        });
    }

    private async void RunSearch()
    {
        var q = _q.Text.Trim();
        if (q.Length == 0) return;
        Info("Mencari\u2026");
        List<LyricData>? list = await _client.SearchAsync(q);
        if (list == null) { Info("Pencarian gagal. Periksa koneksi internet."); return; }
        if (list.Count == 0) { Info("Tidak ada hasil. Coba kata kunci lain."); return; }
        _results.Children.Clear();
        foreach (var item in list) _results.Children.Add(Row(item));
    }

    private UIElement Row(LyricData item)
    {
        string tag = item.Synced.Length > 0 ? "tersinkron" : item.Plain.Length > 0 ? "teks biasa" : "instrumental";
        var sub = string.Join(" \u00B7 ", new[] { item.ArtistName, item.AlbumName, tag }.Where(s => !string.IsNullOrEmpty(s)));

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = item.TrackName ?? "", FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis });

        var border = new Border
        {
            Child = panel, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent, Cursor = Cursors.Hand
        };
        border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) => { _onPick(item); Close(); };
        return border;
    }
}
