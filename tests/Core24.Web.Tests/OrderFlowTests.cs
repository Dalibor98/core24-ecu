using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Core24.Web.Data;
using Core24.Web.Features.Notifications;
using Core24.Web.Features.Orders;
using Core24.Web.Features.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Core24.Web.Tests;

/// <summary>
/// End-to-end: the same HTTP requests a browser would make, from upload to download.
/// </summary>
public class OrderFlowTests : IDisposable
{
    private readonly TestApp app = new();

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Guest_uploads_workshop_delivers_customer_pays_and_downloads()
    {
        var customer = app.Browser();
        var original = RandomBytes(4096);
        var fileName = $"WDB9634031L927285_MCM2_PFLASH+DFLASH_{Md5(original)}_OBD.bin";

        // 1. Customer sends the order.
        var orderLink = await SubmitOrderAsync(customer, "guest@example.com", fileName, original);
        Assert.Contains("We're working on your file", await customer.GetStringAsync(orderLink));

        var order = await LoadOrderAsync();
        Assert.Equal(OrderStatus.Submitted, order.Status);
        Assert.Equal("WDB9634031L927285", order.Vin);
        Assert.True(order.Files.Single().NameChecksumMatches);
        Assert.Equal(29000, order.TotalCents); // the seeded Stage 1 placeholder price

        var emails = app.Emails();
        Assert.Equal(2, emails.Count);
        var workshopMail = emails.Single(e => e.To.ToString().Contains("workshop@core24.local"));
        Assert.Equal(fileName, workshopMail.Attachments.Single().ContentDisposition!.FileName);
        Assert.Equal("guest@example.com", workshopMail.ReplyTo.ToString());

        // 2. Workshop logs in, uploads the modified file, marks the order ready.
        var admin = app.Browser();
        await LoginAsync(admin, "admin@core24.local", "dev-admin-password");
        var adminPage = $"/admin/orders/{order.Id}";
        var token = await admin.GetFormTokenAsync(adminPage);

        var modified = RandomBytes(4096);
        using (var upload = new MultipartFormDataContent())
        {
            upload.Add(new StringContent(token), "__RequestVerificationToken");
            upload.Add(File(modified, "modified.bin"), "modifiedFiles", "modified.bin");
            AssertRedirect(await admin.PostAsync($"{adminPage}?handler=Upload", upload));
        }
        AssertRedirect(await admin.PostFormAsync($"{adminPage}?handler=Ready", token));
        Assert.Equal(OrderStatus.Ready, (await LoadOrderAsync()).Status);

        // 3. Customer can't download before paying.
        var page = await customer.GetStringAsync(orderLink);
        Assert.Contains("Pay €290.00", page);
        Assert.DoesNotContain("/download/", page);

        // 4. Customer pays (through the dev checkout that stands in for Stripe).
        token = await customer.GetFormTokenAsync(orderLink);
        var toCheckout = AssertRedirect(await customer.PostFormAsync($"{orderLink}?handler=Pay", token));
        Assert.StartsWith("/dev/checkout/", toCheckout);

        token = await customer.GetFormTokenAsync(toCheckout);
        AssertRedirect(await customer.PostFormAsync($"{toCheckout}?handler=Pay", token));
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync()).Status);

        // 5. Download unlocked: the bytes are exactly what the workshop uploaded.
        page = await customer.GetStringAsync(orderLink);
        var downloadUrl = Html.Find(page, "href=\"(/download/[^\"]+)\"");
        Assert.Equal(modified, await customer.GetByteArrayAsync(downloadUrl));

        // A tampered link is refused.
        Assert.Equal(HttpStatusCode.Gone, (await customer.GetAsync(downloadUrl + "x")).StatusCode);

        // Received + receipt, ready, paid (customer) + paid (workshop).
        Assert.Equal(5, app.Emails().Count);
    }

    [Fact]
    public async Task Guest_order_moves_into_the_account_once_its_email_is_confirmed()
    {
        var browser = app.Browser();
        var bytes = RandomBytes(1024);
        await SubmitOrderAsync(browser, "Driver@Example.com", "dump.bin", bytes);

        // Register with the same address (different capitalisation on purpose).
        var token = await browser.GetFormTokenAsync("/account/register");
        await browser.PostFormAsync("/account/register", token,
            ("Input.Email", "driver@example.com"), ("Input.Password", "a long enough password"), ("Input.ConfirmPassword", "a long enough password"));

        // Can't log in before confirming.
        var login = await LoginAsync(browser, "driver@example.com", "a long enough password", expectSuccess: false);
        Assert.Contains("confirm your email", login);

        // Follow the link from the confirmation email.
        var mail = app.Emails().Single(e => e.Subject == "Confirm your email");
        var link = Html.Find(mail.HtmlBody!, "href=\"http://localhost(/account/confirm-email[^\"]+)\"");
        Assert.Contains("added them to your account", await browser.GetStringAsync(link));

        await LoginAsync(browser, "driver@example.com", "a long enough password");
        var myOrders = await browser.GetStringAsync("/account");
        Assert.Contains((await LoadOrderAsync()).Reference, myOrders);
    }

    [Fact]
    public async Task Registering_an_existing_email_reveals_nothing_and_emails_the_owner()
    {
        var browser = app.Browser();
        var first = await RegisterAsync(browser, "driver@example.com", "a long enough password");

        // Same email again (unconfirmed account): same answer, the confirmation link is sent again.
        var second = await RegisterAsync(browser, "driver@example.com", "a different password");
        Assert.Equal(Html.Find(first, "<div class=\"notice ok\">([^<]+)"), Html.Find(second, "<div class=\"notice ok\">([^<]+)"));
        Assert.DoesNotContain("already taken", second);
        Assert.Equal(2, app.Emails().Count(e => e.Subject == "Confirm your email"));

        // Confirm, then try again: still the same answer; the owner gets "you already have an account".
        var link = Html.Find(app.Emails().Last(e => e.Subject == "Confirm your email").HtmlBody!, "href=\"http://localhost(/account/confirm-email[^\"]+)\"");
        await browser.GetStringAsync(link);
        Assert.Contains("Almost done", await RegisterAsync(browser, "driver@example.com", "yet another password"));
        Assert.Single(app.Emails(), e => e.Subject == "You already have an account");

        // The first password still works: registering again never changes it.
        await LoginAsync(browser, "driver@example.com", "a long enough password");
    }

    [Fact]
    public void Tests_never_use_real_email_or_payments()
    {
        // Guard: a developer's user secrets (real SMTP, real Stripe) must not leak into test runs.
        using var scope = app.Services.CreateScope();
        Assert.IsType<PickupFolderEmailSender>(scope.ServiceProvider.GetRequiredService<IEmailSender>());
        Assert.IsType<DevPaymentGateway>(scope.ServiceProvider.GetRequiredService<IPaymentGateway>());
    }

    [Fact]
    public async Task Account_menu_knows_who_is_logged_in()
    {
        var browser = app.Browser();
        Assert.Contains("\"signedIn\":false", await browser.GetStringAsync("/api/me"));
        Assert.Contains("Create account", await browser.GetStringAsync("/order/new")); // logged-out menu

        await LoginAsync(browser, "admin@core24.local", "dev-admin-password");
        var me = await browser.GetStringAsync("/api/me");
        Assert.Contains("\"signedIn\":true", me);
        Assert.Contains("\"isAdmin\":true", me);

        var page = await browser.GetStringAsync("/order/new");
        Assert.Contains("admin@core24.local", page);
        Assert.Contains("Price list", page);

        // The landing page's "Log out" is a link (GET): it must only ask, never log out by itself.
        Assert.Contains("Log out?", await browser.GetStringAsync("/account/logout"));
        Assert.Contains("\"signedIn\":true", await browser.GetStringAsync("/api/me"));

        var token = await browser.GetFormTokenAsync("/account/logout");
        await browser.PostFormAsync("/account/logout", token);
        Assert.Contains("\"signedIn\":false", await browser.GetStringAsync("/api/me"));
    }

    [Fact]
    public async Task Admin_pages_need_the_admin_role()
    {
        var anonymous = app.Browser();
        var response = await anonymous.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/login", response.Headers.Location!.PathAndQuery);

        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/admin/files/1")).StatusCode);
    }

    [Fact]
    public async Task Rejects_files_with_the_wrong_extension()
    {
        var browser = app.Browser();
        var token = await browser.GetFormTokenAsync("/order/new");
        using var form = OrderForm(token, "guest@example.com", "virus.exe", RandomBytes(100));
        var response = await browser.PostAsync("/order/new", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the form again, with an error
        Assert.Contains("virus.exe: only .bin, .ori files are accepted.", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Emails());
    }

    [Fact]
    public async Task Stripe_webhook_is_verified_and_safe_to_receive_twice()
    {
        // An order waiting for payment, with a Stripe checkout already started.
        var browser = app.Browser();
        await SubmitOrderAsync(browser, "guest@example.com", "dump.bin", RandomBytes(1024));
        var order = await LoadOrderAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Ready));
            db.Payments.Add(new Payment
            {
                OrderId = order.Id, Provider = "stripe", SessionId = "cs_test_123", CheckoutUrl = "https://checkout.stripe.com/x",
                AmountCents = order.TotalCents, Currency = "EUR", CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var json = $$"""
            {"id":"evt_1","object":"event","type":"checkout.session.completed","created":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"livemode":false,
             "data":{"object":{"id":"cs_test_123","object":"checkout.session","payment_status":"paid","amount_total":{{order.TotalCents}},"currency":"eur" } } }
            """;

        // Forged signature: rejected, nothing changes.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWebhookAsync(json, "wrong_secret")).StatusCode);
        Assert.Equal(OrderStatus.Ready, (await LoadOrderAsync()).Status);

        // Real signature, delivered twice (Stripe does that): paid once, one email.
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(json, TestApp.WebhookSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(json, TestApp.WebhookSecret)).StatusCode);

        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync()).Status);
        Assert.Single(app.Emails(), e => e.Subject!.StartsWith("Payment received"));
    }

    // ---------- helpers ----------

    private async Task<string> SubmitOrderAsync(HttpClient browser, string email, string fileName, byte[] bytes)
    {
        var token = await browser.GetFormTokenAsync("/order/new");
        using var form = OrderForm(token, email, fileName, bytes);
        var location = AssertRedirect(await browser.PostAsync("/order/new", form));
        Assert.StartsWith("/o/", location);
        return location.Split('?')[0];
    }

    private static MultipartFormDataContent OrderForm(string token, string email, string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("Test Driver"), "Input.Name" },
            { new StringContent(email), "Input.Email" },
            { new StringContent("Mercedes-Benz"), "Input.Make" },
            { new StringContent("Actros 1845"), "Input.Model" },
            { new StringContent("1"), "Input.ModIds" }, // seeded "Stage 1 performance remap"
        };
        form.Add(File(bytes, fileName), "Files", fileName);
        return form;
    }

    private static async Task<string> RegisterAsync(HttpClient browser, string email, string password)
    {
        var token = await browser.GetFormTokenAsync("/account/register");
        var response = await browser.PostFormAsync("/account/register", token,
            ("Input.Email", email), ("Input.Password", password), ("Input.ConfirmPassword", password));
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> LoginAsync(HttpClient browser, string email, string password, bool expectSuccess = true)
    {
        var token = await browser.GetFormTokenAsync("/account/login");
        var response = await browser.PostFormAsync("/account/login", token, ("Input.Email", email), ("Input.Password", password));
        if (expectSuccess) AssertRedirect(response);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<Order> LoadOrderAsync()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orders.AsNoTracking().Include(o => o.Files).OrderByDescending(o => o.CreatedAtUtc).FirstAsync();
    }

    /// <summary>Signs like Stripe does: HMAC-SHA256 over "timestamp.body" with the webhook secret.</summary>
    private async Task<HttpResponseMessage> PostWebhookAsync(string json, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{json}")));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/stripe/webhook") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");
        return await app.Browser().SendAsync(request);
    }

    private static string AssertRedirect(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"Expected a redirect, got {(int)response.StatusCode}");
        return response.Headers.Location!.OriginalString;
    }

    private static ByteArrayContent File(byte[] bytes, string name)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);
    private static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes));
}
