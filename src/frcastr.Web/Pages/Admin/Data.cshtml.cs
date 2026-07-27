using frcastr.Core.Enums;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class DataModel(ApplicationDbContext db) : PageModel
{
    /// <summary>One row of the "what's stored" table.</summary>
    public record StoreSummary(string Name, string Description, int Rows, DateTime? Oldest, DateTime? Newest);

    public List<StoreSummary> Stores { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Counts and bounds only — the purge form should never be aimed at a database the admin
        // cannot see the shape of.
        var readings = db.WeatherReadings;
        var hourly = db.WeatherReadingAggregates.Where(a => a.Granularity == AggregationGranularity.Hourly);
        var daily = db.WeatherReadingAggregates.Where(a => a.Granularity == AggregationGranularity.Daily);

        Stores =
        [
            new("Raw readings", "Every individual sample, per channel and device.",
                await readings.CountAsync(ct),
                await readings.MinAsync(r => (DateTime?)r.Timestamp, ct),
                await readings.MaxAsync(r => (DateTime?)r.Timestamp, ct)),

            new("Hourly aggregates", "Min/avg/max per hour, built as raw readings age out.",
                await hourly.CountAsync(ct),
                await hourly.MinAsync(a => (DateTime?)a.PeriodStart, ct),
                await hourly.MaxAsync(a => (DateTime?)a.PeriodStart, ct)),

            new("Daily aggregates", "Min/avg/max per day, built as hourly aggregates age out.",
                await daily.CountAsync(ct),
                await daily.MinAsync(a => (DateTime?)a.PeriodStart, ct),
                await daily.MaxAsync(a => (DateTime?)a.PeriodStart, ct)),

            new("All-time records", "The highs and lows shown on the History page.",
                await db.WeatherChannelRecords.CountAsync(ct),
                await db.WeatherChannelRecords.MinAsync(r => (DateTime?)r.AllTimeMinAt, ct),
                await db.WeatherChannelRecords.MaxAsync(r => (DateTime?)r.AllTimeMaxAt, ct)),

            new("Forecast cache", "Cached provider forecasts; expires on its retention setting.",
                await db.ForecastCaches.CountAsync(ct),
                await db.ForecastCaches.MinAsync(f => (DateTime?)f.FetchedAt, ct),
                await db.ForecastCaches.MaxAsync(f => (DateTime?)f.FetchedAt, ct)),

            new("Alert cache", "Cached severe-weather alerts; expires on its retention setting.",
                await db.AlertCaches.CountAsync(ct),
                await db.AlertCaches.MinAsync(a => (DateTime?)a.FetchedAt, ct),
                await db.AlertCaches.MaxAsync(a => (DateTime?)a.FetchedAt, ct))
        ];
    }
}
