using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace WeatherWidget;

public sealed record LocationInfo(string City, double Latitude, double Longitude);

public sealed record HourlyForecastItem(string HourLabel, double TemperatureC, int WeatherCode, bool IsDay, int PrecipProbability);

public sealed record DailyForecastItem(string DayLabel, int WeatherCode, double MaxTempC, double MinTempC);

public sealed record WeatherData(
    string City,
    double Latitude,
    double Longitude,
    double TemperatureC,
    double FeelsLikeC,
    int HumidityPercent,
    double WindKmh,
    int WeatherCode,
    bool IsDay,
    string SummaryText,
    string ConditionText,
    IReadOnlyList<HourlyForecastItem> Hourly,
    IReadOnlyList<DailyForecastItem> Daily,
    DateTime UpdatedAt);

public static class WeatherService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherWidget/1.0 (Windows 11 Desktop Widget)");
        return client;
    }

    public static async Task<LocationInfo?> DetectLocationByIpAsync(CancellationToken token)
    {
        try
        {
            using var resp = await Http.GetAsync("https://ipwho.is/", token);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token));
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
                    root.TryGetProperty("latitude", out var lat) &&
                    root.TryGetProperty("longitude", out var lon))
                {
                    string city = root.TryGetProperty("city", out var c) && !string.IsNullOrWhiteSpace(c.GetString())
                        ? c.GetString()!
                        : "Mi ubicación";
                    return new LocationInfo(city, lat.GetDouble(), lon.GetDouble());
                }
            }
        }
        catch { }

        try
        {
            using var resp = await Http.GetAsync("http://ip-api.com/json/?fields=status,city,lat,lon", token);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token));
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var st) && st.GetString() == "success")
                {
                    string city = root.TryGetProperty("city", out var c) && !string.IsNullOrWhiteSpace(c.GetString())
                        ? c.GetString()!
                        : "Mi ubicación";
                    return new LocationInfo(city, root.GetProperty("lat").GetDouble(), root.GetProperty("lon").GetDouble());
                }
            }
        }
        catch { }

        return null;
    }

    public static async Task<LocationInfo?> GeocodeCityAsync(string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query.Trim())}&count=1&language=es&format=json";
        using var resp = await Http.GetAsync(url, token);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;

        var first = results[0];
        string name = first.GetProperty("name").GetString() ?? query.Trim();
        double lat = first.GetProperty("latitude").GetDouble();
        double lon = first.GetProperty("longitude").GetDouble();
        return new LocationInfo(name, lat, lon);
    }

    public static async Task<WeatherData> FetchWeatherAsync(LocationInfo location, CancellationToken token)
    {
        string latStr = location.Latitude.ToString(CultureInfo.InvariantCulture);
        string lonStr = location.Longitude.ToString(CultureInfo.InvariantCulture);

        string url =
            $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,is_day,weather_code,wind_speed_10m" +
            "&hourly=temperature_2m,weather_code,precipitation_probability,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
            "&timezone=auto&forecast_days=5";

        using var resp = await Http.GetAsync(url, token);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token));
        var root = doc.RootElement;
        var current = root.GetProperty("current");

        double tempC = current.GetProperty("temperature_2m").GetDouble();
        double feelsLikeC = current.GetProperty("apparent_temperature").GetDouble();
        int humidity = (int)Math.Round(current.GetProperty("relative_humidity_2m").GetDouble());
        bool isDay = current.GetProperty("is_day").GetInt32() == 1;
        int weatherCode = current.GetProperty("weather_code").GetInt32();
        double windKmh = current.GetProperty("wind_speed_10m").GetDouble();

        DateTime currentLocalTime = DateTime.Now;
        if (current.TryGetProperty("time", out var timeEl) &&
            DateTime.TryParse(timeEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedNow))
        {
            currentLocalTime = parsedNow;
        }

        // Pronóstico por hora (próximas 6 horas) y detección de lluvia/llovizna/tormenta en próximas 12 horas
        var hourlyItems = new List<HourlyForecastItem>();
        string? upcomingEventSummary = null;

        if (root.TryGetProperty("hourly", out var hourly))
        {
            var times = hourly.GetProperty("time");
            var temps = hourly.GetProperty("temperature_2m");
            var codes = hourly.GetProperty("weather_code");
            var probs = hourly.GetProperty("precipitation_probability");
            var isDays = hourly.GetProperty("is_day");

            int count = times.GetArrayLength();
            for (int i = 0; i < count; i++)
            {
                if (!DateTime.TryParse(times[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var slotTime))
                    continue;

                if (slotTime <= currentLocalTime) continue;

                double hoursAhead = (slotTime - currentLocalTime).TotalHours;
                int code = codes[i].GetInt32();
                int prob = probs[i].ValueKind == JsonValueKind.Number ? probs[i].GetInt32() : 0;

                if (hourlyItems.Count < 6)
                {
                    hourlyItems.Add(new HourlyForecastItem(
                        slotTime.ToString("HH:00", CultureInfo.InvariantCulture),
                        temps[i].GetDouble(),
                        code,
                        isDays[i].GetInt32() == 1,
                        prob));
                }

                if (upcomingEventSummary == null && hoursAhead <= 12 && !IsPrecipitationCode(weatherCode))
                {
                    string? precipWord = GetUpcomingPrecipitationWord(code);
                    if (precipWord != null)
                    {
                        upcomingEventSummary = $"Espera {precipWord} alrededor de las {slotTime:HH:00}";
                    }
                }
            }
        }

        // Pronóstico diario (5 días)
        var dailyItems = new List<DailyForecastItem>();
        var esCulture = new CultureInfo("es-ES");
        if (root.TryGetProperty("daily", out var daily))
        {
            var dates = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var maxs = daily.GetProperty("temperature_2m_max");
            var mins = daily.GetProperty("temperature_2m_min");

            int count = dates.GetArrayLength();
            for (int i = 0; i < count; i++)
            {
                string label;
                if (i == 0)
                {
                    label = "Hoy";
                }
                else if (DateTime.TryParse(dates[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    string rawDay = d.ToString("ddd", esCulture).TrimEnd('.');
                    label = char.ToUpper(rawDay[0], esCulture) + rawDay[1..];
                }
                else
                {
                    label = dates[i].GetString() ?? "--";
                }

                dailyItems.Add(new DailyForecastItem(
                    label,
                    codes[i].GetInt32(),
                    maxs[i].GetDouble(),
                    mins[i].GetDouble()));
            }
        }

        string conditionText = GetConditionText(weatherCode, isDay);
        string summaryText = upcomingEventSummary ?? conditionText;

        return new WeatherData(
            location.City,
            location.Latitude,
            location.Longitude,
            tempC,
            feelsLikeC,
            humidity,
            windKmh,
            weatherCode,
            isDay,
            summaryText,
            conditionText,
            hourlyItems,
            dailyItems,
            DateTime.Now);
    }

    private static bool IsPrecipitationCode(int code) => code >= 51;

    private static string? GetUpcomingPrecipitationWord(int code) => code switch
    {
        51 or 53 or 55 or 56 or 57 => "llovizna",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "lluvia",
        71 or 73 or 75 or 77 or 85 or 86 => "nieve",
        95 or 96 or 99 => "tormenta",
        _ => null
    };

    public static string GetConditionText(int code, bool isDay) => code switch
    {
        0 => isDay ? "Despejado" : "Noche despejada",
        1 => isDay ? "Mayormente despejado" : "Mayormente despejado",
        2 => "Parcialmente nublado",
        3 => "Nublado",
        45 or 48 => "Niebla",
        51 => "Llovizna ligera",
        53 => "Llovizna moderada",
        55 => "Llovizna intensa",
        56 or 57 => "Llovizna helada",
        61 => "Lluvia ligera",
        63 => "Lluvia moderada",
        65 => "Lluvia fuerte",
        66 or 67 => "Lluvia helada",
        71 or 73 or 75 or 77 => "Nieve",
        80 or 81 => "Chubascos ligeros",
        82 => "Chubascos fuertes",
        85 or 86 => "Chubascos de nieve",
        95 => "Tormenta eléctrica",
        96 or 99 => "Tormenta con granizo",
        _ => "Clima variable"
    };
}
