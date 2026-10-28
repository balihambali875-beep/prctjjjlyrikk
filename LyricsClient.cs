using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace LyricPop;

public sealed class LyricData
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("trackName")] public string? TrackName { get; set; }
    [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
    [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
    [JsonPropertyName("duration")] public double? DurationSec { get; set; }
    [JsonPropertyName("instrumental")] public bool? InstrumentalFlag { get; set; }
    [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
    [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }

    [JsonIgnore] public string Synced => SyncedLyrics ?? "";
    [JsonIgnore] public string Plain => PlainLyrics ?? "";
    [JsonIgnore] public bool IsInstrumental => InstrumentalFlag == true;
    [JsonIgnore] public bool Usable => Synced.Length > 0 || Plain.Length > 0 || IsInstrumental;
}

/// <summary>Klien LRCLIB + cache file (30 hari bila ditemukan, 6 jam bila tidak).</summary>
public sealed class LyricsClient
{
    private const string Api = "https://lrclib.net/api";
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissTtl = TimeSpan.FromHours(6);
    private const int MaxFiles = 300;

    private static readonly HttpClient Http = CreateHttp();
    private readonly string _dir = Path.Combine(AppSettings.DataDir, "cache");

    private sealed class CacheEntry
    {
        public long Ts { get; set; }
        public LyricData? Data { get; set; }
    }

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LyricPop/1.0 (Windows; by arlnxs)");
        h.DefaultRequestHeaders.Add("Lrclib-Client", "LyricPop/1.0 (by arlnxs)");
        return h;
    }

    // ------------------------------------------------------------------ API
    /// <summary>ok=false: gagal jaringan. ok=true &amp; data=null: tidak ditemukan.</summary>
    public async Task<(bool ok, LyricData? data)> GetAsync(Track t, int durSec)
    {
        var key = Key(t);
        var c = ReadCache(key);
        if (c != null)
        {
            var ttl = c.Data != null ? HitTtl : MissTtl;
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - c.Ts < ttl.TotalMilliseconds) return (true, c.Data);
        }

        LyricData? data = null;
        bool failed = false;
        async Task<T?> Attempt<T>(Func<Task<T?>> f) where T : class
        {
            try { return await f(); }
            catch (Exception) { failed = true; return null; }
        }

        var prim = t.Primary.Length > 0 ? t.Primary : t.Artist;

        // 1) Pencocokan persis (butuh album + durasi)
        if (durSec > 0 && t.Album.Length > 0)
        {
            var r = await Attempt(() => GetObj("/get", new()
            {
                ["track_name"] = t.Title, ["artist_name"] = t.Artist,
                ["album_name"] = t.Album, ["duration"] = durSec.ToString()
            }));
            if (r != null && r.Usable) data = r;
        }
        // 2) Cari berdasarkan judul + artis
        data ??= PickBest(await Attempt(() => GetList("/search", new() { ["track_name"] = t.Title, ["artist_name"] = t.Artist })),
            t.Title, t.Artist, durSec);
        // 3) Artis utama saja
        if (data == null && t.Primary.Length > 0 && t.Primary != t.Artist)
            data = PickBest(await Attempt(() => GetList("/search", new() { ["track_name"] = t.Title, ["artist_name"] = t.Primary })),
                t.Title, t.Primary, durSec);
        // 4) Pencarian bebas
        data ??= PickBest(await Attempt(() => GetList("/search", new() { ["q"] = (t.Title + " " + prim).Trim() })),
            t.Title, prim, durSec);

        if (data != null) WriteCache(key, data);
        else if (!failed) WriteCache(key, null); // kegagalan jaringan tidak di-cache
        return (data != null || !failed, data);
    }

    public async Task<List<LyricData>?> SearchAsync(string q)
    {
        try
        {
            var list = await GetList("/search", new() { ["q"] = q });
            return list.Where(d => d.Usable).Take(12).ToList();
        }
        catch (Exception) { return null; }
    }

    public void Save(Track t, LyricData d) => WriteCache(Key(t), d);

    public int ClearCache()
    {
        try
        {
            if (!Directory.Exists(_dir)) return 0;
            var files = Directory.GetFiles(_dir);
            foreach (var f in files) { try { File.Delete(f); } catch (Exception) { /* abaikan */ } }
            return files.Length;
        }
        catch (Exception) { return 0; }
    }

    // ------------------------------------------------------------- pemilihan
    /// <summary>Judul harus mirip; utamakan synced + durasi yang cocok.</summary>
    private static LyricData? PickBest(List<LyricData>? list, string title, string artist, int durSec)
    {
        if (list == null) return null;
        var nt = Txt.Norm(title);
        var na = Txt.Norm(artist);
        var tokens = na.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 1).ToList();
        LyricData? best = null;
        int bestScore = int.MinValue;
        foreach (var r in list)
        {
            if (!r.Usable) continue;
            var rt = Txt.Norm(r.TrackName ?? "");
            var ra = Txt.Norm(r.ArtistName ?? "");
            if (!(rt == nt || rt.Contains(nt) || nt.Contains(rt))) continue;
            if (na.Length > 0 && !tokens.Any(x => ra.Contains(x))) continue;
            int score = 0;
            if (r.Synced.Length > 0) score += 100; else if (r.Plain.Length > 0) score += 20;
            if (rt == nt) score += 30;
            if (ra == na) score += 20;
            if (durSec > 0 && r.DurationSec is double dur && dur > 0)
            {
                double d = Math.Abs(dur - durSec);
                score += d <= 2 ? 40 : d <= 5 ? 15 : d > 15 ? -30 : 0;
            }
            if (r.Synced.Length == 0 && r.Plain.Length == 0) score -= 10;
            if (score > bestScore) { bestScore = score; best = r; }
        }
        return best;
    }

    // ------------------------------------------------------------------ http
    private static async Task<string?> HttpText(string path, Dictionary<string, string> p)
    {
        var q = string.Join("&", p.Where(kv => kv.Value.Length > 0).Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        using var r = await Http.GetAsync($"{Api}{path}?{q}").ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static async Task<LyricData?> GetObj(string path, Dictionary<string, string> p)
    {
        var s = await HttpText(path, p).ConfigureAwait(false);
        return s == null ? null : JsonSerializer.Deserialize<LyricData>(s);
    }

    private static async Task<List<LyricData>?> GetList(string path, Dictionary<string, string> p)
    {
        var s = await HttpText(path, p).ConfigureAwait(false);
        return s == null ? new List<LyricData>() : JsonSerializer.Deserialize<List<LyricData>>(s) ?? new List<LyricData>();
    }

    // ----------------------------------------------------------------- cache
    private static string Key(Track t) => Txt.Norm(t.Artist) + "|" + Txt.Norm(t.Title);

    private string FileFor(string key)
    {
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_dir, h + ".json");
    }

    private CacheEntry? ReadCache(string key)
    {
        try
        {
            var f = FileFor(key);
            return File.Exists(f) ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(f)) : null;
        }
        catch (Exception) { return null; }
    }

    private void WriteCache(string key, LyricData? d)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var e = new CacheEntry { Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Data = d };
            File.WriteAllText(FileFor(key), JsonSerializer.Serialize(e));
            var files = new DirectoryInfo(_dir).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            if (files.Count > MaxFiles)
                foreach (var f in files.Take(files.Count - MaxFiles + 50)) { try { f.Delete(); } catch (Exception) { /* abaikan */ } }
        }
        catch (Exception) { /* abaikan */ }
    }
}
