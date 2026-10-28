using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace LyricPop;

public sealed record Track(string Title, string Artist, string Primary, string Album);
public sealed record LrcLine(double T, string Text);
public readonly record struct Hue(double H, double S);

/// <summary>Pembersih judul/artis, parser LRC, dan ekstraksi warna dominan.</summary>
public static class Txt
{
    private const string Noise =
        @"official|lyrics?|lyric\s*video|lirik|music\s*video|video|audio|visuali[sz]er|mv|m/v|hd|hq|4k|remaster(?:ed)?|explicit|clip|full\s*song|color\s*coded|terjemahan|sub\s*indo|topic";

    private static readonly Regex ReNoiseBracket = new(
        @"\s*[(\[{【「『][^)\]}】」』]*\b(?:" + Noise + @")\b[^)\]}】」』]*[)\]}】」』]", RegexOptions.IgnoreCase);
    private static readonly Regex ReTrail = new(
        @"\s*[-–—|]?\s*(?:official\s*(?:music\s*)?video|official\s*audio|lyric\s*video|lyrics?|audio|visuali[sz]er)\s*$",
        RegexOptions.IgnoreCase);
    private static readonly Regex ReFeat = new(
        @"\s*[(\[]?\s*\b(?:feat|ft|featuring)\b\.?[^)\]]*[)\]]?", RegexOptions.IgnoreCase);
    private static readonly Regex ReRemaster = new(
        @"\s[-–—]\s(?:\d{4}\s)?(?:remaster(?:ed)?|radio edit|single version|mono|stereo|bonus track|from\b).*$",
        RegexOptions.IgnoreCase);
    private static readonly Regex RePipe = new(@"\s+[|｜]\s+");
    private static readonly Regex ReQuotes = new(@"^[""'“”‘’「『]+|[""'“”‘’」』]+$");
    private static readonly Regex ReSpaces = new(@"\s{2,}");
    private static readonly Regex ReArtistSplit = new(
        @"\s*(?:,|;|&|×|\bx\b|\band\b|\bfeat\.?|\bft\.?|\bwith\b|/)\s*", RegexOptions.IgnoreCase);
    private static readonly Regex ReYt = new(@"^(.{1,80}?)\s[-–—]\s(.+)$");
    private static readonly Regex ReTopic = new(@"\s*[-–]\s*topic$", RegexOptions.IgnoreCase);
    private static readonly Regex ReVevo = new(@"\s*vevo$", RegexOptions.IgnoreCase);
    private static readonly Regex ReOfficial = new(@"\s*official$", RegexOptions.IgnoreCase);
    private static readonly Regex ReLrc = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]");
    private static readonly Regex ReWord = new(@"<\d+:\d+(?:[.:]\d+)?>");
    private static readonly Regex ReDiacritic = new(@"[\u0300-\u036f]");
    private static readonly Regex ReNonAlnum = new(@"[^\p{L}\p{N}]+");
    private static readonly Regex ReNewline = new(@"\r?\n");

    public static string CleanTitle(string raw)
    {
        var t = RePipe.Split(raw)[0];
        t = ReNoiseBracket.Replace(t, "");
        t = ReTrail.Replace(t, "", 1);
        t = ReFeat.Replace(t, "", 1);
        t = ReRemaster.Replace(t, "", 1);
        t = ReQuotes.Replace(t, "");
        return ReSpaces.Replace(t, " ").Trim();
    }

    public static string CleanArtist(string a)
    {
        var s = ReTopic.Replace(a, "");
        s = ReVevo.Replace(s, "");
        s = ReOfficial.Replace(s, "");
        return s.Trim();
    }

    public static string PrimaryArtist(string a) => ReArtistSplit.Split(a)[0].Trim();

    public static Track ParseTrack(string title, string artist, string album)
    {
        var t = CleanTitle(title);
        var a = CleanArtist(artist);
        if (a.Length == 0)
        {
            // Tanpa artis: coba format "Artis - Judul"
            var m = ReYt.Match(t);
            if (m.Success) { a = CleanArtist(m.Groups[1].Value); t = CleanTitle(m.Groups[2].Value); }
        }
        if (t.Length == 0) t = title.Trim();
        return new Track(t, a, PrimaryArtist(a), album.Trim());
    }

    public static string Norm(string s)
    {
        var d = ReDiacritic.Replace(s.Normalize(NormalizationForm.FormD), "");
        d = d.Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace("&", " and ");
        return ReNonAlnum.Replace(d, " ").Trim();
    }

    public static string Fmt(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string[] SplitLines(string s) => ReNewline.Split(s);

    /// <summary>Parser LRC: "[mm:ss.xx] teks" (boleh banyak timestamp per baris).</summary>
    public static List<LrcLine> ParseLrc(string text)
    {
        var res = new List<LrcLine>();
        foreach (var raw in ReNewline.Split(text))
        {
            var ms = ReLrc.Matches(raw);
            if (ms.Count == 0) continue; // tag metadata [ar:...] dilewati
            var last = ms[ms.Count - 1];
            var txt = ReWord.Replace(raw.Substring(last.Index + last.Length), "").Trim();
            foreach (Match m in ms)
            {
                var d = m.Groups[3].Value;
                double frac = d.Length == 0 ? 0 : double.Parse("0." + d, CultureInfo.InvariantCulture);
                double t = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + frac;
                res.Add(new LrcLine(t, txt));
            }
        }
        return res.OrderBy(l => l.T).ToList();
    }

    /// <summary>Indeks baris terakhir yang waktunya &lt;= t; -1 bila belum mulai.</summary>
    public static int IndexAt(List<LrcLine> lines, double t)
    {
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].T <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>Warna dominan yang "hidup" dari piksel BGRA.</summary>
    public static Hue DominantHue(byte[] bgra)
    {
        const int B = 24;
        var vx = new double[B]; var vy = new double[B]; var ws = new double[B]; var ss = new double[B];
        double total = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128) continue;
            var (h, s, l) = Pal.RgbToHsl(bgra[i + 2], bgra[i + 1], bgra[i]);
            double w = s * (1 - Math.Abs(2 * l - 1));
            if (w < 0.02) continue;
            int b = (int)(h / (360.0 / B)) % B;
            double rad = h * Math.PI / 180.0;
            vx[b] += w * Math.Cos(rad); vy[b] += w * Math.Sin(rad);
            ws[b] += w; ss[b] += w * s; total += w;
        }
        if (total < 0.5) return new Hue(0, 0); // gambar abu-abu
        int best = 0;
        for (int i = 1; i < B; i++) if (ws[i] > ws[best]) best = i;
        double hue = (Math.Atan2(vy[best], vx[best]) * 180.0 / Math.PI + 360.0) % 360.0;
        return new Hue(hue, ss[best] / ws[best]);
    }
}

/// <summary>Helper warna HSL.</summary>
public static class Pal
{
    public static (double h, double s, double l) RgbToHsl(int r0, int g0, int b0)
    {
        double r = r0 / 255.0, g = g0 / 255.0, b = b0 / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        double l = (mx + mn) / 2, h = 0, s = 0;
        if (mx != mn)
        {
            double d = mx - mn;
            s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
            if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    public static Color Hsl(double h, double s, double l)
    {
        h = (((h % 360) + 360) % 360) / 360.0;
        if (s <= 0) { byte v = (byte)Math.Round(l * 255); return Color.FromRgb(v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double F(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return Color.FromRgb((byte)Math.Round(F(h + 1.0 / 3) * 255), (byte)Math.Round(F(h) * 255), (byte)Math.Round(F(h - 1.0 / 3) * 255));
    }

    public static Color WithAlpha(Color c, double a) =>
        Color.FromArgb((byte)Math.Clamp((int)Math.Round(a * 255), 0, 255), c.R, c.G, c.B);

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
