using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>
/// Музыка через System Media Transport Controls (то, что показывает Windows в оверлее громкости).
/// Работает с Spotify, Яндекс Музыкой, браузерами, VLC и т.д.
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, BitmapSource> _artCache = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _managerAttached;
    private string _artKey = "";
    private bool _busy;
    private bool _started;
    private bool _disposed;
    private DateTimeOffset _positionAt;
    private TimeSpan _basePosition;
    private TimeSpan _duration;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public string Source { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public BitmapSource? Artwork { get; private set; }
    public bool HasSession => !string.IsNullOrEmpty(Title);
    public TimeSpan Duration => _duration;

    public TimeSpan PositionNow
    {
        get
        {
            if (!IsPlaying) return _basePosition;
            var p = _basePosition + (DateTimeOffset.Now - _positionAt);
            return p < TimeSpan.Zero ? TimeSpan.Zero : p > _duration ? _duration : p;
        }
    }

    public event Action? Updated;
    public event Action? TrackChanged;

    public NowPlayingService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Suspend()
    {
        _timer.Stop();
    }

    public void Resume()
    {
        if (!_disposed) _timer.Start();
        _ = RefreshAsync();
    }

    private void Manager_CurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        _dispatcher.BeginInvoke(() => _ = RefreshAsync());
    }

    public async Task RefreshAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager == null) return;

            if (!_managerAttached)
            {
                _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                _managerAttached = true;
            }

            var session = _manager.GetCurrentSession();
            if (!ReferenceEquals(session, _session))
            {
                DetachSession();
                _session = session;
                AttachSession();
            }

            if (session == null)
            {
                if (HasSession || Artwork != null) Clear();
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null) return;

            var playing = false;
            try
            {
                playing = session.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                // некоторые приложения не отдают статус
            }

            try
            {
                var timeline = session.GetTimelineProperties();
                _basePosition = timeline.Position;
                _positionAt = DateTimeOffset.Now;
                _duration = timeline.EndTime > timeline.StartTime
                    ? timeline.EndTime - timeline.StartTime
                    : TimeSpan.Zero;
            }
            catch
            {
                // ignore
            }

            var previousKey = TrackKey();

            Title = props.Title ?? "";
            Artist = props.Artist ?? "";
            Album = props.AlbumTitle ?? "";
            IsPlaying = playing;

            var key = TrackKey();
            if (key != previousKey)
            {
                _artKey = key;
                Artwork = await LoadArtworkAsync(props, key);
            }

            var source = FriendlySource(session.SourceAppUserModelId);
            Source = source;

            Updated?.Invoke();
            if (key != previousKey && !string.IsNullOrEmpty(key))
                TrackChanged?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log("music: " + ex.Message);
            // приложение-источник могло закрыться
            if (ex is System.Runtime.InteropServices.COMException)
            {
                DetachSession();
                _session = null;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void AttachSession()
    {
        if (_session == null) return;
        try
        {
            _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        }
        catch (Exception ex)
        {
            App.Log("attach session: " + ex.Message);
        }
    }

    private void DetachSession()
    {
        if (_session == null) return;
        try
        {
            _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        }
        catch
        {
            // ignore
        }
    }

    private void Session_MediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
        => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private void Session_PlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
        => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private void Session_TimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
        => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private async Task<BitmapSource?> LoadArtworkAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties props,
        string key)
    {
        try
        {
            if (_artCache.TryGetValue(key, out var cached)) return cached;

            var reference = props.Thumbnail;
            if (reference == null) return null;

            using var stream = await reference.OpenReadAsync();
            var length = stream.Size;
            if (length == 0 || length > 12 * 1024 * 1024) return null;

            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)length);
            var bytes = new byte[length];
            reader.ReadBytes(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.DecodePixelWidth = 128;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            if (_artCache.Count >= 4) _artCache.Clear();
            _artCache[key] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            App.Log("artwork: " + ex.Message);
            return null;
        }
    }

    private string TrackKey() => Title + "|" + Artist + "|" + Album;

    private void Clear()
    {
        Title = "";
        Artist = "";
        Album = "";
        Source = "";
        IsPlaying = false;
        Artwork = null;
        _basePosition = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        _artKey = "";
        _artCache.Clear();
        Updated?.Invoke();
    }

    public async Task TogglePlayPauseAsync()
    {
        try
        {
            if (_session != null) await _session.TryTogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            App.Log("toggle: " + ex.Message);
        }
    }

    public async Task NextAsync()
    {
        try
        {
            if (_session != null) await _session.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            App.Log("next: " + ex.Message);
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            if (_session != null) await _session.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            App.Log("prev: " + ex.Message);
        }
    }

    private static string FriendlySource(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        var id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("yandex")) return "Яндекс Музыка";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("aimp")) return "AIMP";
        if (id.Contains("foobar")) return "foobar2000";
        if (id.Contains("music.ui")) return "Медиаплеер";
        if (id.Contains("wmplayer")) return "Windows Media Player";
        if (id.Contains("telegram")) return "Telegram";
        if (id.Contains("zune")) return "Groove";
        if (id.Contains("apple")) return "Apple Music";
        if (id.Contains("tidal")) return "TIDAL";
        if (id.Contains("deezer")) return "Deezer";

        try
        {
            var name = Path.GetFileNameWithoutExtension(appId);
            if (!string.IsNullOrEmpty(name)) return char.ToUpper(name[0]) + name[1..];
        }
        catch
        {
            // ignore
        }

        return "Медиа";
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        if (_managerAttached && _manager != null)
        {
            try
            {
                _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
            }
            catch
            {
                // ignore
            }
            _managerAttached = false;
        }
        DetachSession();
        _session = null;
        _manager = null;
        _artCache.Clear();
    }
}
