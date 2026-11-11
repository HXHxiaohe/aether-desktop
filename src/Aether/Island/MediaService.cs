using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aether.Core;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Aether.Island;

public sealed class MediaSnapshot
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public ImageSource? Cover { get; init; }
    public bool Playing { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset PositionAt { get; init; }
    public string SourceApp { get; init; } = "";
    public bool CanPrevious { get; init; }
    public bool CanNext { get; init; }
    public bool CanSeek { get; init; }
    /// <summary>Read from the player's window title (no Windows media session).</summary>
    public bool FromTitle { get; init; }
    /// <summary>False when we can't know the position (title-only source, song already playing when we started).</summary>
    public bool PositionKnown { get; init; } = true;
    /// <summary>The player gives no timeline; Position comes from our own clock (started with the track).</summary>
    public bool Clock { get; init; }
    public string TrackKey => Title + "\u0001" + Artist;

    /// <summary>Position extrapolated to now while playing.</summary>
    public TimeSpan PositionNow
    {
        get
        {
            if (FromTitle || Clock) return Playing ? Position + (DateTimeOffset.Now - PositionAt) : Position;
            if (!Playing || Duration <= TimeSpan.Zero) return Position;
            var p = Position + (DateTimeOffset.Now - PositionAt);
            return p > Duration ? Duration : p;
        }
    }
}

/// <summary>
/// Windows' "now playing" sessions (Spotify, browsers/YouTube, 网易云, QQ音乐…), event-driven.
/// Events arrive on thread-pool threads; listeners must marshal to the UI themselves.
/// </summary>
public static class MediaService
{
    private static GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private static Session? _session;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string _lastCoverKey = "";
    private static ImageSource? _lastCover;

    public static MediaSnapshot? Current { get; private set; }

    /// <summary>Anything changed (state, position, metadata).</summary>
    public static event Action? Changed;

    /// <summary>A different song started.</summary>
    public static event Action? TrackChanged;

    // ---- title fallback state ----
    private static System.Windows.Threading.DispatcherTimer? _titlePoll;
    private static string _titleKey = "";
    private static bool _titleSeenChange;
    private static TimeSpan _titlePos;
    private static DateTimeOffset _titlePosAt;
    private static bool _titlePlaying;
    private static volatile bool _fallbackActive;

    public static async Task StartAsync()
    {
        // Must be created on the UI thread (before the first await).
        _titlePoll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _titlePoll.Tick += (_, _) => PollTitles();
        _titlePoll.Start();
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => _ = PickAsync();
            _mgr.SessionsChanged += (_, _) => _ = PickAsync();
            await PickAsync();
        }
        catch (Exception ex)
        {
            Log.Error("media-start", ex);
        }
    }

    /// <summary>Prefer a session that is actually playing; else Windows' "current" one.</summary>
    private static async Task PickAsync()
    {
        if (_mgr is null) return;
        Session? pick = null;
        try
        {
            var sessions = _mgr.GetSessions();
            pick = sessions.FirstOrDefault(s => SafeStatus(s) == Status.Playing) ?? _mgr.GetCurrentSession();
        }
        catch (Exception ex) { Log.Error("media-pick", ex); }

        if (!ReferenceEquals(pick, _session))
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnProps;
                _session.PlaybackInfoChanged -= OnPlayback;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            _session = pick;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnProps;
                _session.PlaybackInfoChanged += OnPlayback;
                _session.TimelinePropertiesChanged += OnTimeline;
            }
        }
        await RefreshAsync();
    }

    private static Status SafeStatus(Session s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus; } catch { return Status.Closed; }
    }

    private static void OnProps(Session s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    private static void OnTimeline(Session s, TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

    private static void OnPlayback(Session s, PlaybackInfoChangedEventArgs e)
    {
        // A paused session may lose to another one that just started playing.
        _ = PickAsync();
    }

    // own clock for sessions without a timeline (see RefreshAsync)
    private static string? _clockKey;
    private static TimeSpan _clockElapsed;
    private static DateTimeOffset? _clockSince;
    private static bool _clockFromStart;

    private static async Task RefreshAsync()
    { Aether.Core.Diag.Hit("Media.Refresh");
        await Gate.WaitAsync();
        try
        {
            var s = _session;
            if (s is null)
            {
                if (Current is not null && !_fallbackActive) { Current = null; Changed?.Invoke(); }
                return;
            }

            var info = s.GetPlaybackInfo();
            // A paused browser tab must not replace a title-detected player that is playing.
            if (_fallbackActive && info.PlaybackStatus != Status.Playing) return;
            var props = await s.TryGetMediaPropertiesAsync();
            var tl = s.GetTimelineProperties();
            var key = (props?.Title ?? "") + "\u0001" + (props?.Artist ?? "");

            ImageSource? cover = null;
            if (key == _lastCoverKey && _lastCover is not null) cover = _lastCover;
            else if (props?.Thumbnail is { } thumb)
            {
                try
                {
                    using var stream = await thumb.OpenReadAsync();
                    var bytes = await stream.ReadAllAsync();
                    cover = Decode(bytes);
                    if (cover is not null) { _lastCover = cover; _lastCoverKey = key; }
                }
                catch (Exception ex) { Log.Error("media-cover", ex); }
            }

            var prev = Current;
            bool playing = info.PlaybackStatus == Status.Playing;
            var duration = tl.EndTime - tl.StartTime;
            var position = tl.Position;
            var positionAt = tl.LastUpdatedTime == default ? DateTimeOffset.Now : tl.LastUpdatedTime;
            bool clock = false, known = true;
            if (duration <= TimeSpan.Zero)
            {
                // Players like 网易云 report title/artist but no timeline: keep our own clock that
                // starts with the track and pauses with it, so synced lyrics can still follow.
                var now = DateTimeOffset.Now;
                if (key != _clockKey)
                {
                    _clockKey = key;
                    _clockElapsed = TimeSpan.Zero;
                    _clockSince = playing ? now : null;
                    _clockFromStart = prev is not null; // we saw it change → it started just now
                }
                else if (playing && _clockSince is null) _clockSince = now;
                else if (!playing && _clockSince is { } since) { _clockElapsed += now - since; _clockSince = null; }
                position = _clockElapsed;
                positionAt = _clockSince ?? now;
                clock = true;
                known = _clockFromStart;
            }
            Current = new MediaSnapshot
            {
                Title = props?.Title ?? "",
                Artist = props?.Artist ?? "",
                Album = props?.AlbumTitle ?? "",
                Cover = cover,
                Playing = playing,
                Position = position,
                Duration = duration,
                PositionAt = positionAt,
                Clock = clock,
                PositionKnown = known,
                SourceApp = s.SourceAppUserModelId ?? "",
                CanPrevious = info.Controls.IsPreviousEnabled,
                CanNext = info.Controls.IsNextEnabled,
                CanSeek = info.Controls.IsPlaybackPositionEnabled,
            };
            Changed?.Invoke();
            if (Current.Title.Length > 0 && prev?.TrackKey != Current.TrackKey) TrackChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("media-refresh", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = 160;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>
    /// Runs on the UI thread every 1.5 s: if no Windows media session is playing, look for a
    /// known player showing "歌名 - 歌手" in its title and use that.
    /// </summary>
    private static void PollTitles()
    { Aether.Core.Diag.Hit("Media.PollTitles");
        if (Aether.Shell.GameMode.Active) return; // nothing shows it during fullscreen games
        try
        {
            if (_session is { } s && SafeStatus(s) == Status.Playing)
            {
                if (_fallbackActive) { _fallbackActive = false; _ = RefreshAsync(); }
                return;
            }

            var found = PlayerTitles.Detect();
            if (found is null)
            {
                if (_fallbackActive)
                {
                    _fallbackActive = false;
                    _titleKey = "";
                    Current = null;
                    Changed?.Invoke();
                    _ = RefreshAsync(); // fall back to a paused media session, if any
                }
                return;
            }

            var now = DateTimeOffset.Now;
            bool playing = PlayerTitles.Audible(found.Exe);
            var key = found.Title + "\u0001" + found.Artist;
            bool newTrack = key != _titleKey;
            if (newTrack)
            {
                // Only when we watched the title change do we know the song just started.
                _titleSeenChange = _titleKey.Length > 0;
                _titleKey = key;
                _titlePos = TimeSpan.Zero;
                _titlePosAt = now;
            }
            else if (playing != _titlePlaying)
            {
                if (!playing) _titlePos += now - _titlePosAt; // freeze while paused
                _titlePosAt = now;
            }
            if (!newTrack && playing == _titlePlaying && _fallbackActive) return; // nothing changed
            _titlePlaying = playing;
            _fallbackActive = true;

            Current = new MediaSnapshot
            {
                Title = found.Title,
                Artist = found.Artist,
                Playing = playing,
                Position = _titlePos,
                PositionAt = _titlePosAt,
                SourceApp = System.IO.Path.GetFileName(found.Exe),
                CanPrevious = true,
                CanNext = true,
                FromTitle = true,
                PositionKnown = _titleSeenChange,
            };
            Changed?.Invoke();
            if (newTrack) TrackChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("media-titles", ex);
        }
    }

    // ---------- controls ----------
    private static bool UseKeys => Current?.FromTitle == true;

    public static async void PlayPause() { try { if (UseKeys) PlayerTitles.PlayPause(); else if (_session is { } s) await s.TryTogglePlayPauseAsync(); } catch (Exception ex) { Log.Error("media-toggle", ex); } }
    public static async void Next() { try { if (UseKeys) PlayerTitles.Next(); else if (_session is { } s) await s.TrySkipNextAsync(); } catch (Exception ex) { Log.Error("media-next", ex); } }
    public static async void Previous() { try { if (UseKeys) PlayerTitles.Previous(); else if (_session is { } s) await s.TrySkipPreviousAsync(); } catch (Exception ex) { Log.Error("media-prev", ex); } }

    public static async void Seek(TimeSpan to)
    {
        try { if (_session is { } s) await s.TryChangePlaybackPositionAsync(to.Ticks); }
        catch (Exception ex) { Log.Error("media-seek", ex); }
    }
}
