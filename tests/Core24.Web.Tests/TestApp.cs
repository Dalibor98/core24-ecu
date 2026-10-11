using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Core24.Web.Tests;

/// <summary>
/// Starts the real app in memory (no network, no browser) with its own temporary database,
/// file storage and email folder, so tests never touch your development data.
/// </summary>
public class TestApp : WebApplicationFactory<Program>
{
    public const string WebhookSecret = "whsec_test_secret";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "core24-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // dev gateway + pickup emails + dev admin from appsettings.Development.json
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(Root, "test.db")}");
        builder.UseSetting("Storage:RootPath", Path.Combine(Root, "files"));
        // Tests must never reach real services, whatever the developer has in user secrets
        // (Development loads them too). Force the email folder and fake addresses.
        builder.UseSetting("Email:Mode", "Pickup");
        builder.UseSetting("Email:FromAddress", "orders@core24.local");
        builder.UseSetting("Email:WorkshopAddress", "workshop@core24.local");
        builder.UseSetting("Payments:Provider", "Dev");
        builder.UseSetting("Email:PickupDirectory", Path.Combine(Root, "mail"));
        builder.UseSetting("App:PublicBaseUrl", "http://localhost");
        builder.UseSetting("Payments:StripeWebhookSecret", WebhookSecret);
    }

    /// <summary>A browser-like client: keeps cookies, but lets the test see redirects instead of following them.</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public List<MimeMessage> Emails()
    {
        var folder = Path.Combine(Root, "mail");
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.eml").Order().Select(f => MimeMessage.Load(f)).ToList()
            : [];
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools(); // release the database file so it can be deleted
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public static class Html
{
    /// <summary>Every form has a hidden anti-forgery token; a POST without it is rejected (400).</summary>
    public static async Task<string> GetFormTokenAsync(this HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        return Find(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
    }

    public static string Find(string html, string pattern)
    {
        var m = Regex.Match(html, pattern);
        Assert.True(m.Success, $"Pattern not found: {pattern}");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    public static async Task<HttpResponseMessage> PostFormAsync(this HttpClient client, string url, string token, params (string Key, string Value)[] fields)
    {
        var content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).Append(KeyValuePair.Create("__RequestVerificationToken", token)));
        return await client.PostAsync(url, content);
    }
}
