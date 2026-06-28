using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class WidgetsModel(ApplicationDbContext db) : PageModel
{
    public List<WidgetDefinition> Widgets { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Widgets = await db.WidgetDefinitions.OrderBy(w => w.SortOrder).ToListAsync(ct);
    }

    public static IEnumerable<WidgetType> AllTypes => Enum.GetValues<WidgetType>();

    public static readonly Dictionary<WidgetType, string> TypeNames = new()
    {
        { WidgetType.DateTime,            "Date / Time" },
        { WidgetType.TemperatureOutdoor,  "Temperature (Outdoor)" },
        { WidgetType.TemperatureIndoor,   "Temperature (Indoor)" },
        { WidgetType.HumidityOutdoor,     "Humidity (Outdoor)" },
        { WidgetType.HumidityIndoor,      "Humidity (Indoor)" },
        { WidgetType.Pressure,            "Pressure" },
        { WidgetType.Wind,                "Wind" },
        { WidgetType.WeatherAnimation,    "Weather Animation" },
        { WidgetType.Forecast,            "Daily Forecast" },
        { WidgetType.Moon,                "Moon" },
        { WidgetType.Custom,              "Custom" },
        { WidgetType.SunriseSunset,       "Sunrise / Sunset" },
        { WidgetType.Alerts,              "Alerts" },
        { WidgetType.FeelsLike,           "Feels Like" },
        { WidgetType.Rainfall,            "Rainfall" },
        { WidgetType.PressureTrend,       "Pressure Trend" },
        { WidgetType.AirQuality,          "Air Quality" },
        { WidgetType.Radar,               "Radar" },
        { WidgetType.HourlyForecast,      "Hourly Forecast" },
    };

    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
