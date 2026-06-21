namespace frcastr.Core.Models;

public record WeatherAlert(
    string Event,
    string? Severity,
    string? Headline,
    string? Description,
    DateTime Onset,
    DateTime? Expires);
