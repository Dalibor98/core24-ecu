using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Core24.Web.Data;
using Core24.Web.Features.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Core24.Web.Tests;

/// <summary>
/// Steps that many tests share: send an order, log in, register. Use with
/// <c>using static Core24.Web.Tests.TestFlows;</c> so a test reads like a story.
/// </summary>
public static class TestFlows
{
    public const string AdminEmail = "admin@core24.local";
    public const string AdminPassword = "dev-admin-password";

    /// <summary>Sends the order form like a browser. Returns the private order link (/o/{token}).</summary>
    public static async Task<string> SubmitOrderAsync(HttpClient browser, string email, string fileName, byte[] bytes)
    {
        var token = await browser.GetFormTokenAsync("/order/new");
        using var form = OrderForm(token, email, fileName, bytes);
        var location = AssertRedirect(await browser.PostAsync("/order/new", form));
        Assert.StartsWith("/o/", location);
        return location.Split('?')[0];
    }

    public static MultipartFormDataContent OrderForm(string token, string email, string fileName, byte[] bytes)
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
        form.Add(FilePart(bytes, fileName), "Files", fileName);
        return form;
    }

    public static async Task<string> RegisterAsync(HttpClient browser, string email, string password)
    {
        var token = await browser.GetFormTokenAsync("/account/register");
        var response = await browser.PostFormAsync("/account/register", token,
            ("Input.Email", email), ("Input.Password", password), ("Input.ConfirmPassword", password));
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Registers and clicks the confirmation link, so the account can log in.</summary>
    public static async Task RegisterConfirmedAsync(TestApp app, HttpClient browser, string email, string password)
    {
        await RegisterAsync(browser, email, password);
        var mail = app.Emails().Last(e => e.Subject == "Confirm your email" && e.To.ToString().Contains(email));
        var link = Html.Find(mail.HtmlBody!, "href=\"http://localhost(/account/confirm-email[^\"]+)\"");
        await browser.GetStringAsync(link);
    }

    /// <summary>Posts the login form. Returns the response, so tests can see where it redirects.</summary>
    public static async Task<HttpResponseMessage> PostLoginAsync(HttpClient browser, string email, string password)
    {
        var token = await browser.GetFormTokenAsync("/account/login");
        return await browser.PostFormAsync("/account/login", token, ("Input.Email", email), ("Input.Password", password));
    }

    public static async Task<string> LoginAsync(HttpClient browser, string email, string password, bool expectSuccess = true)
    {
        var response = await PostLoginAsync(browser, email, password);
        if (expectSuccess) AssertRedirect(response);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The newest order in the test database, with its files and payments.</summary>
    public static async Task<Order> LatestOrderAsync(this TestApp app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orders.AsNoTracking().Include(o => o.Files).Include(o => o.Payments)
            .OrderByDescending(o => o.CreatedAtUtc).FirstAsync();
    }

    /// <summary>Runs <paramref name="change"/> on a fresh DbContext: to put the database into a state a test needs.</summary>
    public static async Task WithDbAsync(this TestApp app, Func<AppDbContext, Task> change)
    {
        using var scope = app.Services.CreateScope();
        await change(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static string AssertRedirect(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"Expected a redirect, got {(int)response.StatusCode}");
        return response.Headers.Location!.OriginalString;
    }

    public static ByteArrayContent FilePart(byte[] bytes, string name)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    /// <summary>
    /// Random bytes that look like an ECU dump. The first byte is fixed at 0xFF (common in flash dumps),
    /// so random data can never start with "MZ" or "PK" by chance and be refused as a program or archive.
    /// </summary>
    public static byte[] RandomBytes(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        bytes[0] = 0xFF;
        return bytes;
    }

    public static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes));
}
