namespace frcastr.Core.Interfaces;

public interface IEmailService
{
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
    Task SendToRecipientsAsync(string subject, string htmlBody, CancellationToken ct = default);
}
