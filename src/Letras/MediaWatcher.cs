using global::Windows.Media.Control;

namespace Letras;

public sealed record TrackInfo(string Title, string Artist, string Album)
{
    public string Key => $"{Artist}\u0001{Title}";
}

/// <summary>
/// Lee lo que Windows reporta como "en reproducción" (el mismo panel del volumen).
/// Chrome, Edge, Spotify y la mayoría de reproductores se reportan ahí.
/// </summary>
public sealed class MediaWatcher
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    string? _sessionId;
    int _tick;

    TimeSpan _anchorPos;
    DateTime _anchorAt = DateTime.UtcNow;
    TimeSpan _lastRaw = TimeSpan.MinValue;

    public TrackInfo? Track { get; private set; }
    public bool IsPlaying { get; private set; }
    public TimeSpan Duration { get; private set; }

    /// <summary>Posición estimada de la canción en este instante.</summary>
    public TimeSpan Position => PositionAt(DateTime.UtcNow);

    public async Task StartAsync()
    {
        try { _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { _manager = null; }
    }

    public async Task UpdateAsync()
    {
        if (_manager == null) return;

        var session = PickSession();
        if (session == null)
        {
            Track = null;
            IsPlaying = false;
            _sessionId = null;
            return;
        }

        var id = session.SourceAppUserModelId;
        var sessionChanged = id != _sessionId;
        _sessionId = id;

        if (sessionChanged || Track == null || _tick++ % 4 == 0)
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props != null)
            {
                var t = new TrackInfo(props.Title ?? "", props.Artist ?? "", props.AlbumTitle ?? "");
                if (string.IsNullOrWhiteSpace(t.Title)) t = null;
                if (t == null) Track = null;
                else if (Track == null || t.Key != Track.Key)
                {
                    Track = t;
                    ResetTiming();
                }
            }
        }

        var now = DateTime.UtcNow;

        var info = session.GetPlaybackInfo();
        var playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (playing != IsPlaying)
        {
            // Congela o reanuda el reloj en el punto exacto del cambio.
            _anchorPos = PositionAt(now);
            _anchorAt = now;
            IsPlaying = playing;
        }

        var tl = session.GetTimelineProperties();
        if (tl == null) return;

        var duration = tl.EndTime - tl.StartTime;
        if (duration > TimeSpan.Zero) Duration = duration;

        if (tl.Position != _lastRaw)
        {
            var first = _lastRaw == TimeSpan.MinValue;
            _lastRaw = tl.Position;

            // Si el reproductor no reporta tiempos (todo en cero), usamos nuestro propio reloj.
            var reportsTimeline = tl.Position > TimeSpan.Zero || tl.EndTime > TimeSpan.Zero;
            if (!(first && !reportsTimeline))
            {
                var pos = tl.Position;
                var age = now - tl.LastUpdatedTime.UtcDateTime;
                var maxAge = Duration > TimeSpan.Zero ? Duration : TimeSpan.FromMinutes(10);
                if (IsPlaying && age > TimeSpan.Zero && age < maxAge) pos += age;
                _anchorPos = pos;
                _anchorAt = now;
            }
        }
    }

    TimeSpan PositionAt(DateTime now) =>
        IsPlaying ? _anchorPos + (now - _anchorAt) : _anchorPos;

    void ResetTiming()
    {
        _anchorPos = TimeSpan.Zero;
        _anchorAt = DateTime.UtcNow;
        _lastRaw = TimeSpan.MinValue;
        Duration = TimeSpan.Zero;
    }

    GlobalSystemMediaTransportControlsSession? PickSession()
    {
        var sessions = _manager!.GetSessions();

        // Prioridad: la sesión que ya seguíamos si sigue sonando, luego cualquiera que esté sonando.
        GlobalSystemMediaTransportControlsSession? playing = null;
        foreach (var s in sessions)
        {
            var status = s.GetPlaybackInfo()?.PlaybackStatus;
            if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
            if (s.SourceAppUserModelId == _sessionId) return s;
            playing ??= s;
        }
        if (playing != null) return playing;

        foreach (var s in sessions)
            if (s.SourceAppUserModelId == _sessionId) return s;

        return _manager.GetCurrentSession();
    }
}
