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
        // AR-12: alert bodies name files, users and administrative actions, so the hop to the
        // relay is encrypted where the relay offers it. Both settings are configurable because
        // an internal relay may support neither.
        using var client = new SmtpClient(host, int.TryParse(cfg["Smtp:Port"], out var p) ? p : 25)
        {
            EnableSsl = !string.Equals(cfg["Smtp:UseTls"], "false", StringComparison.OrdinalIgnoreCase),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        if (!string.IsNullOrEmpty(cfg["Smtp:User"]))
            client.Credentials = new System.Net.NetworkCredential(cfg["Smtp:User"], cfg["Smtp:Password"]);
        using var msg = new MailMessage(cfg["Smtp:From"] ?? "bc-inventory@localhost", to)
        {
            Subject = "[BC Inventory] " + subject,
            Body = body + "\n\n—\nBC Inventory Reporting System (local test)\nhttp://localhost:8088"
        };
        client.Send(msg);
    }
}
