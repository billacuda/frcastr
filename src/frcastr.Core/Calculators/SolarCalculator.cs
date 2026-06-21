using frcastr.Core.Models;

namespace frcastr.Core.Calculators;

/// <summary>
/// NOAA solar position algorithm — pure math, no external API.
/// Reference: https://gml.noaa.gov/grad/solcalc/calcdetails.html
/// </summary>
public static class SolarCalculator
{
    private const double SunriseZenith = 90.833; // geometric + refraction + semi-diameter
    private const double GoldenHourZenith = 84.0;  // sun 6° above horizon

    public static SolarInfo Calculate(double latitude, double longitude, DateOnly date, TimeZoneInfo tz)
    {
        // Julian Day for UTC noon on this date
        var utcNoon = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Utc);
        var jd = ToJulianDay(utcNoon);

        var t = (jd - 2451545.0) / 36525.0;

        var l0 = NormalizeAngle(280.46646 + t * (36000.76983 + t * 0.0003032));
        var m = NormalizeAngle(357.52911 + t * (35999.05029 - t * 0.0001537));
        var e = 0.016708634 - t * (0.000042037 + t * 0.0000001267);
        var mRad = DegToRad(m);

        var c = (1.914602 - t * (0.004817 + t * 0.000014)) * Math.Sin(mRad)
              + (0.019993 - t * 0.000101) * Math.Sin(2 * mRad)
              + 0.000289 * Math.Sin(3 * mRad);

        var sunTrueLon = l0 + c;
        var omega = 125.04 - 1934.136 * t;
        var lambda = sunTrueLon - 0.00569 - 0.00478 * Math.Sin(DegToRad(omega));

        var epsilon0 = 23.0 + (26.0 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60.0) / 60.0;
        var epsilon = epsilon0 + 0.00256 * Math.Cos(DegToRad(omega));
        var lambdaRad = DegToRad(lambda);
        var epsilonRad = DegToRad(epsilon);

        var declRad = Math.Asin(Math.Sin(epsilonRad) * Math.Sin(lambdaRad));

        // Equation of time (minutes)
        var y = Math.Pow(Math.Tan(epsilonRad / 2.0), 2);
        var l0Rad = DegToRad(l0);
        var eqTime = 4.0 * RadToDeg(
            y * Math.Sin(2 * l0Rad)
            - 2 * e * Math.Sin(mRad)
            + 4 * e * y * Math.Sin(mRad) * Math.Cos(2 * l0Rad)
            - 0.5 * y * y * Math.Sin(4 * l0Rad)
            - 1.25 * e * e * Math.Sin(2 * mRad));

        // Solar noon in minutes from UTC midnight
        var solarNoonMinutes = 720.0 - 4.0 * longitude - eqTime;

        // Hour angles
        var latRad = DegToRad(latitude);
        var haRise = CalcHourAngle(latRad, declRad, SunriseZenith);
        var haGolden = CalcHourAngle(latRad, declRad, GoldenHourZenith);

        DateTimeOffset ToDto(double minutesFromMidnightUtc)
        {
            var utcDt = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc)
                .AddMinutes(minutesFromMidnightUtc);
            return TimeZoneInfo.ConvertTime(new DateTimeOffset(utcDt), tz);
        }

        var noon = ToDto(solarNoonMinutes);
        DateTimeOffset? sunrise = null, sunset = null, goldenMorning = null, goldenEvening = null;
        var dayLength = TimeSpan.Zero;

        if (double.IsFinite(haRise))
        {
            sunrise = ToDto(solarNoonMinutes - haRise * 4.0);
            sunset = ToDto(solarNoonMinutes + haRise * 4.0);
            dayLength = sunset.Value - sunrise.Value;
        }

        if (double.IsFinite(haGolden))
        {
            goldenMorning = ToDto(solarNoonMinutes - haGolden * 4.0);
            goldenEvening = ToDto(solarNoonMinutes + haGolden * 4.0);
        }

        return new SolarInfo(sunrise, noon, sunset, goldenMorning, goldenEvening, dayLength);
    }

    private static double CalcHourAngle(double latRad, double declRad, double zenithDeg)
    {
        var zenithRad = DegToRad(zenithDeg);
        var cosHA = (Math.Cos(zenithRad) / (Math.Cos(latRad) * Math.Cos(declRad)))
                  - Math.Tan(latRad) * Math.Tan(declRad);
        if (cosHA < -1 || cosHA > 1) return double.NaN; // polar day/night
        return RadToDeg(Math.Acos(cosHA));
    }

    private static double ToJulianDay(DateTime utc)
    {
        // Algorithm from https://en.wikipedia.org/wiki/Julian_day
        int y = utc.Year, m = utc.Month, d = utc.Day;
        if (m <= 2) { y--; m += 12; }
        var a = (int)(y / 100.0);
        var b = 2 - a + (int)(a / 4.0);
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5
             + (utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0) / 24.0;
    }

    private static double NormalizeAngle(double deg) => deg - 360.0 * Math.Floor(deg / 360.0);
    private static double DegToRad(double d) => d * Math.PI / 180.0;
    private static double RadToDeg(double r) => r * 180.0 / Math.PI;
}
