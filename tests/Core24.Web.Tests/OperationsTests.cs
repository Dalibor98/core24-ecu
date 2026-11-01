using System.Net;
using System.Net.Http.Json;
using Core24.Web.Features.Maintenance;
using Core24.Web.Features.Orders;
using Core24.Web.Features.Payments;
using Core24.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Core24.Web.Tests.TestFlows;

namespace Core24.Web.Tests;

/// <summary>Refunds, background jobs, the booking form, security headers, health, logs and alerts.</summary>
public class OperationsTests : IDisposable
{
    private readonly TestApp app = new();

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Admin_refunds_a_paid_order_and_the_download_locks_again()
    {
        var (customer, orderLink) = await PaidOrderAsync();
        var page = await customer.GetStringAsync(orderLink);
        var downloadUrl = Html.Find(page, "href=\"(/download/[^\"]+)\"");

        var order = await app.LatestOrderAsync();
        var payment = order.Payments.Single();
        var admin = await AdminAsync();
        var adminPage = $"/admin/orders/{order.Id}";
        var token = await admin.GetFormTokenAsync(adminPage);
        AssertRedirect(await admin.PostFormAsync($"{adminPage}?handler=Refund&paymentId={payment.Id}", token));

        order = await app.LatestOrderAsync();
        Assert.Equal(OrderStatus.Refunded, order.Status);
        Assert.Equal(PaymentStatus.Refunded, order.Payments.Single().Status);
        Assert.StartsWith("dev_refund_", order.Payments.Single().RefundId);
        Assert.Single(app.Emails(), e => e.Subject!.StartsWith("Refund for order"));

        // The customer sees the refund, and the old download link stops working.
        Assert.Contains("This order was refunded", await customer.GetStringAsync(orderLink));
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync(downloadUrl)).StatusCode);

        // A second click can't refund twice.
        AssertRedirect(await admin.PostFormAsync($"{adminPage}?handler=Refund&paymentId={payment.Id}", token));
        Assert.Contains("Only a successful payment can be refunded.", await admin.GetStringAsync(adminPage));
        Assert.Single(app.Emails(), e => e.Subject!.StartsWith("Refund for order"));
    }

    [Fact]
    public async Task Unpaid_orders_get_two_reminders_and_no_more()
    {
        await SubmitOrderAsync(app.Browser(), "slow@example.com", "dump.bin", RandomBytes(1024));
        var order = await app.LatestOrderAsync();
        await SetReadyAsync(order.Id, readyDaysAgo: 2);

        Assert.Equal(0, await RunAsync(m => m.SendPaymentRemindersAsync())); // too early

        await SetReadyAsync(order.Id, readyDaysAgo: 4);
        Assert.Equal(1, await RunAsync(m => m.SendPaymentRemindersAsync()));
        Assert.Equal(0, await RunAsync(m => m.SendPaymentRemindersAsync())); // the next hourly run must not repeat it

        await SetReadyAsync(order.Id, readyDaysAgo: 8);
        Assert.Equal(1, await RunAsync(m => m.SendPaymentRemindersAsync()));
        Assert.Equal(0, await RunAsync(m => m.SendPaymentRemindersAsync()));

        Assert.Equal(2, app.Emails().Count(e => e.Subject!.StartsWith("Reminder:")));
        Assert.Equal(2, (await app.LatestOrderAsync()).PaymentRemindersSent);
    }

    [Fact]
    public async Task Files_of_finished_orders_are_deleted_after_the_retention_period()
    {
        var (customer, orderLink) = await PaidOrderAsync();
        var order = await app.LatestOrderAsync();
        var store = app.Services.GetRequiredService<IFileStore>();
        var paths = order.Files.Select(f => store.GetFullPath(f.StorageKey)).ToList();
        Assert.All(paths, p => Assert.True(File.Exists(p)));

        Assert.Equal(0, await RunAsync(m => m.DeleteExpiredFilesAsync())); // paid just now: kept

        await app.WithDbAsync(db => db.Orders.Where(o => o.Id == order.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.PaidAtUtc, DateTime.UtcNow.AddDays(-91))));
        Assert.Equal(1, await RunAsync(m => m.DeleteExpiredFilesAsync()));

        Assert.All(paths, p => Assert.False(File.Exists(p)));
        order = await app.LatestOrderAsync();
        Assert.NotNull(order.FilesDeletedAtUtc);
        Assert.Equal(2, order.Files.Count); // the records stay: name, size, MD5
        Assert.Contains("Its files were deleted", await customer.GetStringAsync(orderLink));
        Assert.Equal(0, await RunAsync(m => m.DeleteExpiredFilesAsync())); // done once, not again
    }

    [Fact]
    public async Task Booking_form_emails_the_workshop_and_ignores_bots()
    {
        var browser = app.Browser();
        var ok = await browser.PostAsJsonAsync("/api/contact", new { name = "Ivo", phone = "+385 91 000 0000", make = "Volvo", model = "FH 500", issue = "Engine light <b>on</b>" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var mail = app.Emails().Single();
        Assert.Equal("Booking request: Ivo, Volvo", mail.Subject);
        Assert.Contains("+385 91 000 0000", mail.HtmlBody);
        Assert.Contains("&lt;b&gt;on&lt;/b&gt;", mail.HtmlBody); // what visitors type is encoded, never run as HTML

        // A bot fills in the hidden "website" field: it gets "OK", the workshop gets nothing.
        var bot = await browser.PostAsJsonAsync("/api/contact", new { name = "Bot", phone = "1", website = "http://spam.example" });
        Assert.Equal(HttpStatusCode.OK, bot.StatusCode);
        Assert.Single(app.Emails());

        var missingPhone = await browser.PostAsJsonAsync("/api/contact", new { name = "Ivo" });
        Assert.Equal(HttpStatusCode.BadRequest, missingPhone.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_program_renamed_to_bin()
    {
        var browser = app.Browser();
        var token = await browser.GetFormTokenAsync("/order/new");
        var exe = new byte[1024];
        "MZ"u8.CopyTo(exe);
        using var form = OrderForm(token, "guest@example.com", "dump.bin", exe);

        var response = await browser.PostAsync("/order/new", form);
        Assert.Contains("dump.bin is a Windows program, not an ECU file.", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Emails());
    }

    [Fact]
    public async Task Every_page_sends_security_headers()
    {
        var browser = app.Browser();

        var page = await browser.GetAsync("/order/new");
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self';", csp);                      // no inline scripts on app pages
        Assert.Contains("form-action 'self' https://checkout.stripe.com", csp);
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());

        var landing = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        Assert.Contains("https://cdn.jsdelivr.net", landing.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Health_endpoint_checks_database_and_storage()
    {
        var response = await app.Browser().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Request_log_never_contains_order_tokens()
    {
        var browser = app.Browser();
        var orderLink = await SubmitOrderAsync(browser, "guest@example.com", "dump.bin", RandomBytes(1024));
        await browser.GetStringAsync(orderLink);
        var secret = orderLink["/o/".Length..];

        var log = ReadLogs();
        Assert.Contains("HTTP GET /o/{token} responded 200", log);
        Assert.DoesNotContain(secret, log);
    }

    [Fact]
    public async Task Errors_are_emailed_to_the_developer()
    {
        using var alerting = new TestApp(("Alerts:To", "dev@example.com"), ("Alerts:BatchDelay", "00:00:00"), ("Alerts:Cooldown", "00:00:00"));
        var logger = alerting.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

        logger.LogError("Something broke for order {Reference}", "A1B2C3D4");

        // The alert is sent by a background service: wait for it (at most 10 seconds).
        for (var i = 0; i < 100 && !alerting.Emails().Any(e => e.To.ToString() == "dev@example.com"); i++)
            await Task.Delay(100);
        var alert = alerting.Emails().Single(e => e.To.ToString() == "dev@example.com");
        Assert.StartsWith("CORE24: ", alert.Subject);
        Assert.Contains("Something broke for order", alert.HtmlBody);
        Assert.Contains("A1B2C3D4", alert.HtmlBody);
    }

    // ---------- helpers ----------

    /// <summary>A guest order that went all the way to Paid. Returns the customer's browser and order link.</summary>
    private async Task<(HttpClient Customer, string OrderLink)> PaidOrderAsync()
    {
        var customer = app.Browser();
        var orderLink = await SubmitOrderAsync(customer, "guest@example.com", "dump.bin", RandomBytes(2048));
        var order = await app.LatestOrderAsync();

        var admin = await AdminAsync();
        var adminPage = $"/admin/orders/{order.Id}";
        var token = await admin.GetFormTokenAsync(adminPage);
        using (var upload = new MultipartFormDataContent())
        {
            upload.Add(new StringContent(token), "__RequestVerificationToken");
            upload.Add(FilePart(RandomBytes(2048), "modified.bin"), "modifiedFiles", "modified.bin");
            AssertRedirect(await admin.PostAsync($"{adminPage}?handler=Upload", upload));
        }
        AssertRedirect(await admin.PostFormAsync($"{adminPage}?handler=Ready", token));

        token = await customer.GetFormTokenAsync(orderLink);
        var checkout = AssertRedirect(await customer.PostFormAsync($"{orderLink}?handler=Pay", token));
        token = await customer.GetFormTokenAsync(checkout);
        AssertRedirect(await customer.PostFormAsync($"{checkout}?handler=Pay", token));
        Assert.Equal(OrderStatus.Paid, (await app.LatestOrderAsync()).Status);
        return (customer, orderLink);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = app.Browser();
        await LoginAsync(admin, AdminEmail, AdminPassword);
        return admin;
    }

    private Task SetReadyAsync(Guid orderId, int readyDaysAgo) =>
        app.WithDbAsync(db => db.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.Status, OrderStatus.Ready)
            .SetProperty(o => o.ReadyAtUtc, DateTime.UtcNow.AddDays(-readyDaysAgo))));

    private async Task<int> RunAsync(Func<Maintenance, Task<int>> job)
    {
        using var scope = app.Services.CreateScope();
        return await job(scope.ServiceProvider.GetRequiredService<Maintenance>());
    }

    private string ReadLogs()
    {
        // FileShare.ReadWrite: Serilog still has the file open for writing.
        return string.Concat(Directory.GetFiles(Path.Combine(app.Root, "logs")).Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(stream).ReadToEnd();
        }));
    }
}
