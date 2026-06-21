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
}
