namespace frcastr.Core.Interfaces;

public interface ISetupService
{
    Task<bool> IsSetupCompleteAsync(CancellationToken ct = default);
    Task<bool> IsDatabaseConfiguredAsync(CancellationToken ct = default);
    Task<bool> IsAdminCreatedAsync(CancellationToken ct = default);
    Task<bool> IsStationConfiguredAsync(CancellationToken ct = default);
    Task<bool> IsBrandingConfiguredAsync(CancellationToken ct = default);
    Task SetupDatabaseAsync(string server, string database, string? username, string? password, CancellationToken ct = default);
    Task CompleteSetupAsync(string adminEmail, string adminPassword, CancellationToken ct = default);
    Task SaveStationAsync(string name, decimal latitude, decimal longitude, decimal elevation, string timezone, CancellationToken ct = default);
    Task SaveEmailAsync(string? smtpHost, int port, bool useSsl, string? username, string? password, string? fromAddress, CancellationToken ct = default);
    Task SaveBrandingAsync(string appName, string primaryColor, CancellationToken ct = default);
    Task FinalizeSetupAsync(CancellationToken ct = default);
}
