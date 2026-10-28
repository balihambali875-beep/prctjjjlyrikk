using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace LyricPop;

/// <summary>
/// Membaca lagu yang sedang diputar dari Windows (System Media Transport Controls),
/// sumber yang sama dengan panel volume Windows: Spotify desktop, YouTube Music, browser, dll.
/// </summary>
public sealed class MediaWatcher
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private Session? _session;
    private string? _sessionId;
    private readonly object _gate = new();
    private readonly Stopwatch _pickClock = Stopwatch.StartNew();

    private TimeSpan _basePos;
    private DateTimeOffset _baseTime = DateTimeOffset.Now;
    private double _rate = 1.0;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public string AppId { get; private set; } = "";
    public byte[]? Thumb { get; private set; }
    /// <summary>Naik setiap kali metadata/thumbnail dimuat ulang.</summary>
    public int ThumbGen { get; private set; }
    public bool Playing { get; private set; }
    public bool CanPrev { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanSeek { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool HasMeta => Title.Length > 0 || Artist.Length > 0;

    public async Task InitAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        Pick();
    }

    /// <summary>Dipanggil rutin dari timer UI: pilih sesi, lalu baca status & posisi.</summary>
    public void Poll()
    {
        if (_mgr == null) return;
        if (_pickClock.ElapsedMilliseconds > 1000) { _pickClock.Restart(); Pick(); }
        var s = _session;
        if (s == null) return;
        try
        {
            var pb = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();
            lock (_gate)
            {
                Playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                CanPrev = pb.Controls.IsPreviousEnabled;
                CanNext = pb.Controls.IsNextEnabled;
                CanSeek = pb.Controls.IsPlaybackPositionEnabled;
                _rate = pb.PlaybackRate ?? 1.0;
                _basePos = tl.Position - tl.StartTime;
                _baseTime = tl.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : tl.LastUpdatedTime;
                Duration = tl.EndTime - tl.StartTime;
            }
        }
        catch (Exception) { /* sesi baru saja ditutup */ }
    }

    /// <summary>Posisi putar saat ini (diekstrapolasi dari laporan terakhir pemutar).</summary>
    public TimeSpan Position()
    {
        lock (_gate)
        {
            var p = _basePos;
            if (Playing) p += TimeSpan.FromSeconds((DateTimeOffset.Now - _baseTime).TotalSeconds * _rate);
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    // ------------------------------------------------------------ pilih sesi
    private void Pick()
    {
        if (_mgr == null) return;
        Session? best = null;
        try
        {
            var all = _mgr.GetSessions();
            // Prioritas: yang sedang memutar > sesi yang sudah dipakai > sesi aktif Windows > mana saja
            best = all.FirstOrDefault(s =>
            {
                try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
                catch (Exception) { return false; }
            });
            best ??= all.FirstOrDefault(s => s.SourceAppUserModelId == _sessionId);
            best ??= _mgr.GetCurrentSession();
            best ??= all.FirstOrDefault();
        }
        catch (Exception) { /* abaikan */ }

        if (_session != null && best?.SourceAppUserModelId == _sessionId) return; // sesi sama
        Rebind(best);
    }

    private void Rebind(Session? s)
    {
        if (_session != null) _session.MediaPropertiesChanged -= OnProps;
        _session = s;
        _sessionId = s?.SourceAppUserModelId;
        AppId = _sessionId ?? "";
        if (s != null)
        {
            s.MediaPropertiesChanged += OnProps;
            _ = RefreshPropsAsync(s);
        }
        else
        {
            lock (_gate) { Title = ""; Artist = ""; Album = ""; Thumb = null; ThumbGen++; Playing = false; }
        }
    }

    private void OnProps(Session s, MediaPropertiesChangedEventArgs a) => _ = RefreshPropsAsync(s);

    private async Task RefreshPropsAsync(Session s)
    {
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            if (!ReferenceEquals(s, _session) && s.SourceAppUserModelId != _sessionId) return;

            byte[]? bytes = null;
            if (p.Thumbnail != null)
            {
                try
                {
                    using var rs = await p.Thumbnail.OpenReadAsync();
                    var buf = new byte[rs.Size];
                    using var reader = new DataReader(rs.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)rs.Size);
                    reader.ReadBytes(buf);
                    bytes = buf;
                }
                catch (Exception) { bytes = null; }
            }
            lock (_gate)
            {
                Title = p.Title ?? ""; Artist = p.Artist ?? ""; Album = p.AlbumTitle ?? "";
                Thumb = bytes; ThumbGen++;
            }
        }
        catch (Exception) { /* abaikan */ }
    }

    // --------------------------------------------------------------- kontrol
    public async Task TogglePlayPauseAsync() { var s = _session; if (s != null) try { await s.TryTogglePlayPauseAsync(); } catch (Exception) { } }
    public async Task NextAsync() { var s = _session; if (s != null) try { await s.TrySkipNextAsync(); } catch (Exception) { } }
    public async Task PrevAsync() { var s = _session; if (s != null) try { await s.TrySkipPreviousAsync(); } catch (Exception) { } }

    public async Task SeekAsync(TimeSpan to)
    {
        var s = _session;
        if (s == null) return;
        lock (_gate) { _basePos = to; _baseTime = DateTimeOffset.Now; } // optimistis
        try { await s.TryChangePlaybackPositionAsync((to + TimeSpan.Zero).Ticks); } catch (Exception) { }
    }
}
