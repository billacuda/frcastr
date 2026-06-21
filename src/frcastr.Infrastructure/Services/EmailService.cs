using System.Net;
using System.Net.Mail;
using frcastr.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Services;

public class EmailService(
    ISettingsService settings,
    ILogger<EmailService> logger) : IEmailService
{
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => await settings.GetBoolAsync("Email.OutboundConfigured", false, ct);

    public async Task SendAsync(string to, string subject, string htmlBody,
        CancellationToken ct = default)
    {
        if (!await IsConfiguredAsync(ct)) return;

        var host = await settings.GetAsync("Email.SmtpHost", ct);
        var port = await settings.GetIntAsync("Email.SmtpPort", 587, ct);
        var useSsl = await settings.GetBoolAsync("Email.SmtpUseSsl", true, ct);
        var username = await settings.GetAsync("Email.SmtpUsername", ct);
        var password = await settings.GetAsync("Email.SmtpPassword", ct);
        var from = await settings.GetAsync("Email.FromAddress", ct);

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from)) return;

        try
        {
            using var client = new SmtpClient(host, port)
            {
                EnableSsl = useSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            if (!string.IsNullOrWhiteSpace(username))
                client.Credentials = new NetworkCredential(username, password);

            using var msg = new MailMessage(from, to, subject, htmlBody) { IsBodyHtml = true };
            await client.SendMailAsync(msg, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email to {To}.", to);
            throw;
        }
    }

    public async Task SendToRecipientsAsync(string subject, string htmlBody,
        CancellationToken ct = default)
    {
        var recipients = await settings.GetAsync("Email.DigestRecipients", ct);
        if (string.IsNullOrWhiteSpace(recipients)) return;

        foreach (var addr in recipients.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try { await SendAsync(addr, subject, htmlBody, ct); }
            catch { /* already logged inside SendAsync */ }
        }
    }
}
