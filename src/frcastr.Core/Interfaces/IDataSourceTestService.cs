namespace frcastr.Core.Interfaces;

public record DataSourceTestResult(bool Success, string? Error, object? Sample);

public interface IDataSourceTestService
{
    Task<DataSourceTestResult> TestAsync(int sourceId, CancellationToken ct = default);
}
