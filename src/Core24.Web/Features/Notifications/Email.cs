using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Core24.Web.Features.Notifications;

public sealed record EmailAttachment(string FileName, string FullPath);

public sealed record EmailMessage(string To, string Subject, string HtmlBody)
{
    public string? ReplyTo { get; init; }
    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
}

/// <summary>Sends one email. Two implementations: real SMTP, and a folder on disk for development.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public class EmailOptions
{
    public const string Section = "Email";

    /// <summary>"Pickup" writes .eml files to a folder (development). "Smtp" really sends.</summary>
    public string Mode { get; set; } = "Smtp";

    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "CORE24";

    /// <summary>The workshop's inbox (my client). Receives new orders and payment notices.</summary>
    public string WorkshopAddress { get; set; } = "";

    public string PickupDirectory { get; set; } = "App_Data/mail";

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
}

internal static class MimeFactory
{
    public static MimeMessage Create(EmailMessage message, EmailOptions options)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        if (message.ReplyTo is not null) mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        mime.Subject = message.Subject;

        var body = new BodyBuilder { HtmlBody = message.HtmlBody };
        foreach (var attachment in message.Attachments)
            body.Attachments.Add(attachment.FileName, File.ReadAllBytes(attachment.FullPath));
        mime.Body = body.ToMessageBody();
        return mime;
    }
}

/// <summary>
/// Real sending through any SMTP service: Resend, Postmark, Brevo, or the host's own mail server.
/// SMTP is the common denominator, so switching provider is a settings change, not a code change.
/// </summary>
public class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var o = options.Value;
        using var client = new SmtpClient();
        await client.ConnectAsync(o.SmtpHost, o.SmtpPort, SecureSocketOptions.StartTlsWhenAvailable, ct);
        if (!string.IsNullOrEmpty(o.SmtpUsername))
            await client.AuthenticateAsync(o.SmtpUsername, o.SmtpPassword, ct);
        await client.SendAsync(MimeFactory.Create(message, o), ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}

/// <summary>
/// Development only: writes each email as an .eml file instead of sending it.
/// Double-click one to open it in Outlook or Thunderbird, attachments included.
/// </summary>
public class PickupFolderEmailSender(IOptions<EmailOptions> options, IWebHostEnvironment env, ILogger<PickupFolderEmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var folder = Path.Combine(env.ContentRootPath, options.Value.PickupDirectory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.eml");
        await MimeFactory.Create(message, options.Value).WriteToAsync(path, ct);
        log.LogInformation("Email to {To} \"{Subject}\" written to {Path}", message.To, message.Subject, path);
    }
}
