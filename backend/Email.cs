using System.Net.Mail;

namespace BcInventory.Api;

/// <summary>SMTP alert e-mails (FR-N1 e-mail channel). Local testing targets the Mailpit container.</summary>
public static class Email
{
    public static bool Configured(IConfiguration cfg) => !string.IsNullOrEmpty(cfg["Smtp:Host"]);

    public static void Send(IConfiguration cfg, string to, string subject, string body)
    {
        var host = cfg["Smtp:Host"];
        if (string.IsNullOrEmpty(host)) throw new InvalidOperationException("SMTP not configured");
        using var client = new SmtpClient(host, int.TryParse(cfg["Smtp:Port"], out var p) ? p : 25)
        {
            EnableSsl = false,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        using var msg = new MailMessage(cfg["Smtp:From"] ?? "bc-inventory@localhost", to)
        {
            Subject = "[BC Inventory] " + subject,
            Body = body + "\n\n—\nBC Inventory Reporting System (local test)\nhttp://localhost:8088"
        };
        client.Send(msg);
    }
}
