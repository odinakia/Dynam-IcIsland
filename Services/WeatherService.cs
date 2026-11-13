using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace DynamicIsland.Services;

public sealed record WeatherInfo(
    double Temperature,
    double FeelsLike,
    double Min,
    double Max,
    int Code,
    string City,
    bool IsDay,
    double Wind,
    DateTime UpdatedAt);

/// <summary>
/// Погода через Open-Meteo (без API-ключа). Город: из настроек или по IP.
/// Результат кешируется на диск, чтобы при старте не ждать сеть.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly string CachePath = Path.Combine(App.DataDir, "weather.json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly Settings _settings;
    private readonly DispatcherTimer _timer;
    private bool _busy;
    private bool _disposed;
    private double? _lat;
    private double? _lon;
    private string _resolvedCity = "";
    private string? _resolvedFor;

    public WeatherInfo? Current { get; private set; }
    public event Action? Updated;

    public WeatherService(Settings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(15),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        LoadCache();
    }

    public void Start()
    {
        if (_disposed) return;
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            var cityChanged = !string.Equals(_resolvedFor, _settings.City, StringComparison.OrdinalIgnoreCase);
            if (force || _lat == null || _lon == null || cityChanged)
            {
                if (!await ResolveLocationAsync()) return;
            }

            var url = "https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={_lat!.Value.ToString(CultureInfo.InvariantCulture)}" +
                      $"&longitude={_lon!.Value.ToString(CultureInfo.InvariantCulture)}" +
                      "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m" +
                      "&daily=temperature_2m_max,temperature_2m_min" +
                      "&timezone=auto&forecast_days=1";

            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                App.Log($"weather http {(int)response.StatusCode}");
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");

            var temp = current.GetProperty("temperature_2m").GetDouble();
            var feels = current.TryGetProperty("apparent_temperature", out var f) ? f.GetDouble() : temp;
            var wind = current.TryGetProperty("wind_speed_10m", out var w) ? w.GetDouble() : 0;
            var code = current.GetProperty("weather_code").GetInt32();
            var isDay = !current.TryGetProperty("is_day", out var d) || d.GetInt32() != 0;
            var min = daily.GetProperty("temperature_2m_min")[0].GetDouble();
            var max = daily.GetProperty("temperature_2m_max")[0].GetDouble();

            Current = new WeatherInfo(temp, feels, min, max, code, _resolvedCity, isDay, wind, DateTime.Now);
            SaveCache();
            Updated?.Invoke();
            _timer.Interval = TimeSpan.FromMinutes(15);
        }
        catch (Exception ex)
        {
            App.Log("weather: " + ex.Message);
            _timer.Interval = TimeSpan.FromMinutes(3);
        }
        finally
        {
            _busy = false;
            if (!_disposed) _timer.Start();
        }
    }

    private async Task<bool> ResolveLocationAsync()
    {
        _resolvedFor = _settings.City;

        if (!string.IsNullOrWhiteSpace(_settings.City))
        {
            try
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=ru&format=json&name=" +
                          Uri.EscapeDataString(_settings.City.Trim());
                using var response = await Http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("results", out var results) &&
                        results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
                    {
                        var first = results[0];
                        _lat = first.GetProperty("latitude").GetDouble();
                        _lon = first.GetProperty("longitude").GetDouble();
                        _resolvedCity = first.TryGetProperty("name", out var n)
                            ? n.GetString() ?? _settings.City
                            : _settings.City;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log("geocode: " + ex.Message);
            }
        }

        // по IP
        try
        {
            using var response = await Http.GetAsync("https://ipapi.co/json/");
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("latitude", out var lat) &&
                    lat.ValueKind == JsonValueKind.Number &&
                    doc.RootElement.TryGetProperty("longitude", out var lon) &&
                    lon.ValueKind == JsonValueKind.Number)
                {
                    _lat = lat.GetDouble();
                    _lon = lon.GetDouble();
                    _resolvedCity = doc.RootElement.TryGetProperty("city", out var c)
                        ? c.GetString() ?? ""
                        : "";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("ip geo: " + ex.Message);
        }

        // резервный сервис
        try
        {
            using var response = await Http.GetAsync(
                "http://ip-api.com/json/?fields=status,city,lat,lon,country");
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var status) && status.GetString() == "success")
                {
                    _lat = root.GetProperty("lat").GetDouble();
                    _lon = root.GetProperty("lon").GetDouble();
                    _resolvedCity = root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("ip geo 2: " + ex.Message);
        }

        return false;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var root = doc.RootElement;
            _lat = root.GetProperty("lat").GetDouble();
            _lon = root.GetProperty("lon").GetDouble();
            _resolvedCity = root.GetProperty("city").GetString() ?? "";
            _resolvedFor = root.GetProperty("for").GetString() ?? "";
            Current = new WeatherInfo(
                root.GetProperty("temp").GetDouble(),
                root.GetProperty("feels").GetDouble(),
                root.GetProperty("min").GetDouble(),
                root.GetProperty("max").GetDouble(),
                root.GetProperty("code").GetInt32(),
                _resolvedCity,
                root.GetProperty("day").GetBoolean(),
                root.GetProperty("wind").GetDouble(),
                root.GetProperty("at").GetDateTime());
        }
        catch
        {
            // кеш необязателен
        }
    }

    private void SaveCache()
    {
        if (Current == null || _lat == null || _lon == null) return;
        try
        {
            var payload = new
            {
                lat = _lat.Value,
                lon = _lon.Value,
                city = _resolvedCity,
                @for = _resolvedFor,
                temp = Current.Temperature,
                feels = Current.FeelsLike,
                min = Current.Min,
                max = Current.Max,
                code = Current.Code,
                day = Current.IsDay,
                wind = Current.Wind,
                at = Current.UpdatedAt,
            };
            File.WriteAllText(CachePath, JsonSerializer.Serialize(payload));
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }

    public static (string Glyph, string Description) Describe(int code, bool isDay)
    {
        return code switch
        {
            0 => (isDay ? "☀️" : "🌙", "ясно"),
            1 => (isDay ? "🌤️" : "🌙", "почти ясно"),
            2 => (isDay ? "⛅" : "☁️", "переменная облачность"),
            3 => ("☁️", "пасмурно"),
            45 or 48 => ("🌫️", "туман"),
            51 or 53 or 55 or 56 or 57 => ("🌦️", "морось"),
            61 or 63 or 65 or 66 or 67 => ("🌧️", "дождь"),
            71 or 73 or 75 or 77 => ("🌨️", "снег"),
            80 or 81 or 82 => ("🌦️", "ливень"),
            85 or 86 => ("🌨️", "снегопад"),
            95 or 96 or 99 => ("⛈️", "гроза"),
            _ => ("🌡️", ""),
        };
    }
}
