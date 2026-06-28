using frcastr.Core.Models;

namespace frcastr.Core.Calculators;

/// <summary>
/// Moon phase from epoch arithmetic — pure math, no external API.
/// Epoch: 2000-01-06 18:14 UTC (J2000.0 new moon).
/// </summary>
public static class MoonPhaseCalculator
{
    private static readonly DateTime Epoch = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);
    private const double SynodicPeriod = 29.53058867;

    public static MoonPhaseInfo Calculate(DateTime utcNow)
    {
        var daysSinceEpoch = (utcNow - Epoch).TotalDays;
        var phase = (daysSinceEpoch % SynodicPeriod + SynodicPeriod) % SynodicPeriod / SynodicPeriod;
        var illumination = (1.0 - Math.Cos(phase * 2.0 * Math.PI)) / 2.0;
        var (name, icon) = PhaseName(phase);
        return new MoonPhaseInfo(phase, name, illumination, icon);
    }

    public static MoonPhaseInfo CalculateWithRiseSet(
        DateTime utcNow, double latitude, double longitude, TimeZoneInfo tz)
    {
        var baseInfo = Calculate(utcNow);
        var date = DateOnly.FromDateTime(utcNow);
        var (moonrise, moonset) = ComputeRiseSet(latitude, longitude, date, tz);
        return baseInfo with { Moonrise = moonrise, Moonset = moonset };
    }

    private static (DateTimeOffset? Rise, DateTimeOffset? Set) ComputeRiseSet(
        double latitude, double longitude, DateOnly date, TimeZoneInfo tz)
    {
        var jd = JulianDay(new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc));
        var d  = jd - 2451545.0;

        // Moon's mean elements (Jean Meeus, simplified)
        var L = Norm(218.316 + 13.176396 * d);
        var M = Norm(134.963 + 13.064993 * d);
        var F = Norm( 93.272 + 13.229350 * d);

        var lambdaDeg = L + 6.289 * Math.Sin(Rad(M));
        var betaDeg   = 5.128 * Math.Sin(Rad(F));

        const double Eps = 23.4393;
        var sinDecl = Math.Sin(Rad(betaDeg)) * Math.Cos(Rad(Eps))
                    + Math.Cos(Rad(betaDeg)) * Math.Sin(Rad(Eps)) * Math.Sin(Rad(lambdaDeg));
        var decl = Math.Asin(Math.Clamp(sinDecl, -1.0, 1.0));

        var ra = Norm(Deg(Math.Atan2(
            Math.Sin(Rad(lambdaDeg)) * Math.Cos(Rad(Eps)) - Math.Tan(Rad(betaDeg)) * Math.Sin(Rad(Eps)),
            Math.Cos(Rad(lambdaDeg)))));

        // Greenwich Mean Sidereal Time at 0h UT
        var T    = d / 36525.0;
        var gmst = Norm(100.4606184 + 36000.770 * T + 0.000387933 * T * T);
        var lmst = Norm(gmst + longitude);

        // Hour angle at transit (in hours from 0h UT)
        var transit = Norm(ra - lmst) / 15.0;

        // Half-hour-angle at rise/set (moon zenith: 90.567° includes parallax + semi-diameter)
        const double Zenith = 90.567;
        var cosH = (Math.Cos(Rad(Zenith)) - Math.Sin(Rad(latitude)) * Math.Sin(decl))
                 / (Math.Cos(Rad(latitude)) * Math.Cos(decl));

        if (cosH is < -1.0 or > 1.0) return (null, null);

        var H = Deg(Math.Acos(cosH)) / 15.0;

        return (ToDto(Wrap24(transit - H)), ToDto(Wrap24(transit + H)));

        DateTimeOffset ToDto(double hours)
        {
            var utc = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc)
                .AddHours(hours);
            return TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), tz);
        }
    }

    private static (string Name, string Icon) PhaseName(double phase) => phase switch
    {
        < 0.025 => ("New Moon", "🌑"),
        < 0.25  => ("Waxing Crescent", "🌒"),
        < 0.275 => ("First Quarter", "🌓"),
        < 0.50  => ("Waxing Gibbous", "🌔"),
        < 0.525 => ("Full Moon", "🌕"),
        < 0.75  => ("Waning Gibbous", "🌖"),
        < 0.775 => ("Last Quarter", "🌗"),
        _       => ("Waning Crescent", "🌘")
    };

    private static double JulianDay(DateTime utc)
    {
        int y = utc.Year, m = utc.Month, d = utc.Day;
        if (m <= 2) { y--; m += 12; }
        var a = y / 100;
        var b = 2 - a + a / 4;
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5
             + (utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0) / 24.0;
    }

    private static double Norm(double d) => (d % 360 + 360) % 360;
    private static double Wrap24(double h) => (h % 24 + 24) % 24;
    private static double Rad(double d) => d * Math.PI / 180.0;
    private static double Deg(double r) => r * 180.0 / Math.PI;
}
