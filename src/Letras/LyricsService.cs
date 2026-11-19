using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Letras;

public sealed record LyricsResult(List<LrcLine>? Synced, string? Plain, bool Instrumental)
{
    public static readonly LyricsResult None = new(null, null, false);
}

sealed record LrcLibItem(
    int Id, string? TrackName, string? ArtistName, double Duration,
    bool Instrumental, string? PlainLyrics, string? SyncedLyrics);

/// <summary>Busca letras sincronizadas en LRCLIB (lrclib.net), una base de datos libre y gratuita.</summary>
public sealed class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly Dictionary<string, LyricsResult> _cache = new();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Letras/0.1 (+https://github.com/mariamnievesdesign/lyrics)");
        return http;
    }

    public async Task<LyricsResult> FindAsync(TrackInfo track, TimeSpan duration, CancellationToken ct)
    {
        if (_cache.TryGetValue(track.Key, out var cached)) return cached;

        var (artist, title) = Clean(track);
        var found = new List<LrcLibItem>();

        if (artist.Length > 0)
            found.AddRange(await SearchAsync($"api/search?track_name={E(title)}&artist_name={E(artist)}", ct));

        if (!found.Any(HasSynced))
            found.AddRange(await SearchAsync($"api/search?q={E($"{artist} {title}".Trim())}", ct));

        var seconds = duration.TotalSeconds;
        var best = found
            .Where(HasSynced)
            .OrderBy(i => seconds > 0 && i.Duration > 0 ? Math.Abs(i.Duration - seconds) : 0)
            .FirstOrDefault();

        LyricsResult result;
        if (best != null)
            result = new LyricsResult(Lrc.Parse(best.SyncedLyrics!), best.PlainLyrics, false);
        else
        {
            var plain = found.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.PlainLyrics));
            var instrumental = found.Count > 0 && found.All(i => i.Instrumental);
            result = new LyricsResult(null, plain?.PlainLyrics, instrumental);
        }

        _cache[track.Key] = result;
        return result;
    }

    static bool HasSynced(LrcLibItem i) => !string.IsNullOrWhiteSpace(i.SyncedLyrics);

    static string E(string s) => WebUtility.UrlEncode(s);

    static async Task<List<LrcLibItem>> SearchAsync(string url, CancellationToken ct)
    {
        using var res = await Http.GetAsync(url, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return new();
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<LrcLibItem>>(Json, ct) ?? new();
    }

    // ---- Limpieza de títulos (YouTube suele traer "(Official Video)", "Artista - Topic", etc.) ----

    static readonly Regex Noise = new(
        @"\s*[\(\[][^\)\]]*(official|oficial|video|vídeo|lyric|letra|audio|visuali[sz]er|\bhd\b|4k|\bmv\b|remaster|en vivo|live)[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Feat = new(@"\s*[\(\[]\s*(feat|ft|con|with)\.?\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Suffix = new(@"\s+[-–—]\s+[^-–—]*(remaster|version|versión|live|edit|mix)[^-–—]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Split = new(@"^(.+?)\s+[-–—]\s+(.+)$", RegexOptions.Compiled);

    static (string artist, string title) Clean(TrackInfo t)
    {
        var title = Noise.Replace(t.Title, "");
        title = Feat.Replace(title, "");
        title = Suffix.Replace(title, "").Trim();

        var artist = t.Artist.Trim();
        var looksLikeChannel = artist.Length == 0
            || Regex.IsMatch(artist, @"(\s-\sTopic|VEVO|Official|Oficial)$", RegexOptions.IgnoreCase);
        artist = Regex.Replace(artist, @"\s*-\s*Topic$|VEVO$", "", RegexOptions.IgnoreCase).Trim();

        // En videos de YouTube el título suele ser "Artista - Canción".
        var m = Split.Match(title);
        if (m.Success)
        {
            var left = m.Groups[1].Value.Trim();
            var matchesArtist = artist.Length > 0 &&
                (left.Contains(artist, StringComparison.OrdinalIgnoreCase) || artist.Contains(left, StringComparison.OrdinalIgnoreCase));
            if (matchesArtist || looksLikeChannel)
            {
                artist = left;
                title = m.Groups[2].Value.Trim();
            }
        }

        return (artist, title);
    }
}
