using System.Net;
using System.Text;
using Core24.Web.Features.Orders;
using Core24.Web.Features.Storage;
using Microsoft.Extensions.Options;

namespace Core24.Web.Features.Notifications;

/// <summary>
/// Knows WHICH emails the business sends and what they say. <see cref="IEmailSender"/> only knows HOW to send.
/// </summary>
public class Notifier(
    IEmailSender sender,
    IFileStore files,
    IOptions<EmailOptions> emailOptions,
    IOptions<AppOptions> appOptions,
    ILogger<Notifier> log)
{
    // Providers cap message size (Postmark: 10 MB). Above this, the workshop downloads from the admin page instead.
    private const long MaxAttachmentBytes = 9 * 1024 * 1024;

    private EmailOptions Email => emailOptions.Value;
    private AppOptions App => appOptions.Value;

    /// <summary>To the workshop: a new order with specs and the original files. To the customer: a receipt with their private link.</summary>
    public async Task OrderSubmittedAsync(Order order, CancellationToken ct = default)
    {
        var originals = order.OriginalFiles.ToList();
        var attach = originals.Sum(f => f.SizeBytes) <= MaxAttachmentBytes;

        var workshop = new StringBuilder()
            .Append(P($"New order <b>{H(order.Reference)}</b> from {H(order.CustomerName)}."))
            .Append(OrderSummaryHtml(order))
            .Append(attach
                ? P("The original files are attached.")
                : P("The files are too large to attach. Download them from the admin page."))
            .Append(Button("Open in admin", App.Link($"/admin/orders/{order.Id}")))
            .Append(P("Reply to this email to answer the customer directly."));

        await SendSafelyAsync(new EmailMessage(Email.WorkshopAddress, $"New order {order.Reference}: {order.VehicleMake} {order.Vin}", Layout(workshop.ToString()))
        {
            ReplyTo = order.CustomerEmail,
            Attachments = attach ? originals.Select(f => new EmailAttachment(f.FileName, files.GetFullPath(f.StorageKey))).ToList() : [],
        }, ct);

        var customer = new StringBuilder()
            .Append(P($"Hello {H(order.CustomerName)},"))
            .Append(P($"we received your order <b>{H(order.Reference)}</b>. We'll email you as soon as the modified file is ready."))
            .Append(OrderSummaryHtml(order))
            .Append(Button("View your order", OrderLink(order)))
            .Append(P("Keep this email: the button is your private link to the order."));

        await SendSafelyAsync(new EmailMessage(order.CustomerEmail, $"Order {order.Reference} received", Layout(customer.ToString())), ct);
    }

    /// <summary>To the customer: the file is ready, please pay.</summary>
    public async Task OrderReadyAsync(Order order, CancellationToken ct = default)
    {
        var body = new StringBuilder()
            .Append(P($"Hello {H(order.CustomerName)},"))
            .Append(P($"your modified file for order <b>{H(order.Reference)}</b> is ready."))
            .Append(P($"Total: <b>{H(Money.Format(order.TotalCents, order.Currency))}</b>. The download unlocks as soon as the payment is confirmed."))
            .Append(Button("Pay and download", OrderLink(order)));

        await SendSafelyAsync(new EmailMessage(order.CustomerEmail, $"Your file is ready: order {order.Reference}", Layout(body.ToString())), ct);
    }

    /// <summary>To the customer: download unlocked. To the workshop: you've been paid.</summary>
    public async Task OrderPaidAsync(Order order, CancellationToken ct = default)
    {
        var customer = new StringBuilder()
            .Append(P($"Thank you, {H(order.CustomerName)}. We received your payment for order <b>{H(order.Reference)}</b>."))
            .Append(Button("Download your file", OrderLink(order)));
        await SendSafelyAsync(new EmailMessage(order.CustomerEmail, $"Payment received: order {order.Reference}", Layout(customer.ToString())), ct);

        var workshop = P($"Order <b>{H(order.Reference)}</b> was paid: {H(Money.Format(order.TotalCents, order.Currency))}.")
            + Button("Open in admin", App.Link($"/admin/orders/{order.Id}"));
        await SendSafelyAsync(new EmailMessage(Email.WorkshopAddress, $"Paid: order {order.Reference}", Layout(workshop)), ct);
    }

    public async Task ConfirmEmailAsync(string to, string confirmLink, CancellationToken ct = default)
    {
        var body = P("Please confirm your email address to finish creating your CORE24 account.")
            + Button("Confirm my email", confirmLink)
            + P("Orders you placed earlier with this address will appear in your account once it's confirmed.");
        await SendSafelyAsync(new EmailMessage(to, "Confirm your email", Layout(body)), ct);
    }

    /// <summary>Someone tried to register with an address that already has a confirmed account.</summary>
    public async Task AccountExistsAsync(string to, string loginLink, CancellationToken ct = default)
    {
        var body = P("Someone (probably you) tried to create a CORE24 account with this email address, but you already have one.")
            + Button("Log in", loginLink)
            + P("If this wasn't you, you can ignore this email. Nothing was changed.");
        await SendSafelyAsync(new EmailMessage(to, "You already have an account", Layout(body)), ct);
    }

    private string OrderLink(Order order) => App.Link($"/o/{order.AccessToken}");

    /// <summary>
    /// A failed email must never undo an order or a payment, so errors are logged, not thrown.
    /// (A sturdier design is an "outbox": save the email in the database and retry in the background.)
    /// </summary>
    private async Task SendSafelyAsync(EmailMessage message, CancellationToken ct)
    {
        try
        {
            await sender.SendAsync(message, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Sending email \"{Subject}\" to {To} failed", message.Subject, message.To);
        }
    }

    private static string OrderSummaryHtml(Order order)
    {
        var sb = new StringBuilder("<table cellpadding=\"6\" style=\"border-collapse:collapse;font-size:14px\">");
        Row("Reference", order.Reference);
        Row("Customer", $"{order.CustomerName} <{order.CustomerEmail}>");
        if (order.CustomerPhone is not null) Row("Phone", order.CustomerPhone);
        Row("Vehicle", $"{order.VehicleMake} {order.VehicleModel}".Trim());
        if (order.Vin is not null) Row("VIN", order.Vin);
        Row("Mods", string.Join(", ", order.Items.Select(i => $"{i.ModName} ({Money.Format(i.PriceCents, order.Currency)})")));
        Row("Total", Money.Format(order.TotalCents, order.Currency));
        if (order.CustomerNote is not null) Row("Message", order.CustomerNote);
        sb.Append("</table>");

        foreach (var f in order.OriginalFiles)
        {
            sb.Append("<p style=\"font-size:13px\"><b>").Append(H(f.FileName)).Append("</b><br>")
              .Append(H($"{f.SizeBytes / 1024.0:N0} KB · MD5 {f.Md5}"));
            if (f.EcuType is not null)
                sb.Append("<br>").Append(H($"ECU: {EcuFileName.DescribeEcu(f.EcuType)} · Memory: {EcuFileName.DescribeMemory(f.MemoryArea)} · Read: {EcuFileName.DescribeReadMethod(f.ReadMethod)}"));
            sb.Append("<br>").Append(f.NameChecksumMatches switch
            {
                true => "Checksum in the file name matches the file.",
                false => "<b style=\"color:#b42318\">Warning: the checksum in the file name does NOT match the file. It may be damaged or already modified.</b>",
                null => "File name has no checksum to compare.",
            });
            sb.Append("</p>");
        }
        return sb.ToString();

        void Row(string label, string value) =>
            sb.Append("<tr><td style=\"color:#667085\">").Append(H(label)).Append("</td><td>").Append(H(value)).Append("</td></tr>");
    }

    // Everything the customer typed goes through H(): HTML-encoding stops them injecting markup into our emails.
    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "");
    private static string P(string html) => $"<p>{html}</p>";

    private static string Button(string text, string url) =>
        $"<p><a href=\"{H(url)}\" style=\"display:inline-block;padding:10px 18px;border-radius:999px;background:#06080b;color:#fff;text-decoration:none\">{H(text)}</a></p>";

    private static string Layout(string inner) =>
        $"<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:15px;color:#101828;max-width:600px\"><p style=\"font-weight:800;letter-spacing:.05em\">CORE24</p>{inner}</div>";
}
