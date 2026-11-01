using System.Net;
using System.Security.Cryptography;
using static Core24.Web.Tests.TestFlows;

namespace Core24.Web.Tests;

/// <summary>Password reset, password change and two-factor login.</summary>
public class AccountSecurityTests : IDisposable
{
    private readonly TestApp app = new();

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Forgot_password_emails_a_one_time_link_that_sets_a_new_password()
    {
        var browser = app.Browser();
        await RegisterConfirmedAsync(app, browser, "driver@example.com", "the old password");

        var token = await browser.GetFormTokenAsync("/account/forgot-password");
        var sent = await browser.PostFormAsync("/account/forgot-password", token, ("Input.Email", "driver@example.com"));
        Assert.Contains("a reset link is on its way", await sent.Content.ReadAsStringAsync());

        var mail = app.Emails().Single(e => e.Subject == "Reset your password");
        var link = Html.Find(mail.HtmlBody!, "href=\"http://localhost(/account/reset-password[^\"]+)\"");

        var done = await ResetAsync(browser, link, "the new password");
        Assert.Contains("Your password was changed", done);
        Assert.Single(app.Emails(), e => e.Subject == "Your password was changed");

        await LoginAsync(app.Browser(), "driver@example.com", "the new password");
        Assert.Contains("Wrong email or password", await LoginAsync(app.Browser(), "driver@example.com", "the old password", expectSuccess: false));

        // The link worked once. The password change replaced the security stamp inside the token, so it's dead now.
        Assert.Contains("invalid or has expired", await ResetAsync(browser, link, "a third password!"));
    }

    [Fact]
    public async Task Forgot_password_reveals_nothing_about_unknown_emails()
    {
        var browser = app.Browser();
        var token = await browser.GetFormTokenAsync("/account/forgot-password");
        var response = await browser.PostFormAsync("/account/forgot-password", token, ("Input.Email", "nobody@example.com"));

        // Same answer as for a real account, and no email goes out.
        Assert.Contains("a reset link is on its way", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Emails());
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_keeps_you_logged_in()
    {
        var browser = app.Browser();
        await RegisterConfirmedAsync(app, browser, "driver@example.com", "the old password");
        await LoginAsync(browser, "driver@example.com", "the old password");

        var token = await browser.GetFormTokenAsync("/account/password");
        var wrong = await browser.PostFormAsync("/account/password", token,
            ("Input.CurrentPassword", "not my password"), ("Input.NewPassword", "the new password"), ("Input.ConfirmPassword", "the new password"));
        Assert.Contains("The current password is wrong.", await wrong.Content.ReadAsStringAsync());

        AssertRedirect(await browser.PostFormAsync("/account/password", token,
            ("Input.CurrentPassword", "the old password"), ("Input.NewPassword", "the new password"), ("Input.ConfirmPassword", "the new password")));
        Assert.Contains("Password changed.", await browser.GetStringAsync("/account/password"));

        Assert.Contains("\"signedIn\":true", await browser.GetStringAsync("/api/me")); // RefreshSignInAsync kept this browser logged in
        Assert.Single(app.Emails(), e => e.Subject == "Your password was changed");
        await LoginAsync(app.Browser(), "driver@example.com", "the new password");
    }

    [Fact]
    public async Task Account_settings_need_a_login()
    {
        var anonymous = app.Browser();
        foreach (var page in new[] { "/account/password", "/account/two-factor" })
        {
            var response = await anonymous.GetAsync(page);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/account/login", response.Headers.Location!.PathAndQuery);
        }
    }

    [Fact]
    public async Task Admin_pages_require_a_two_factor_login_when_switched_on()
    {
        using var strict = new TestApp(("Admin:RequireTwoFactor", "true"));

        // 1. Password only: logged in, but the admin pages say no and point to the setup.
        var admin = strict.Browser();
        await LoginAsync(admin, AdminEmail, AdminPassword);
        var denied = await admin.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/account/denied", denied.Headers.Location!.PathAndQuery);
        Assert.Contains("Set up two-factor login", await admin.GetStringAsync("/account/denied"));

        // 2. Set up: read the key from the page (as the phone app would from the QR code), answer with a code.
        var setup = await admin.GetStringAsync("/account/two-factor");
        var key = Html.Find(setup, "Type this key instead: <span class=\"mono\">([A-Z2-7 ]+)</span>").Replace(" ", "");
        var token = Html.Find(setup, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

        var wrong = await admin.PostFormAsync("/account/two-factor?handler=Enable", token, ("Input.Code", "000000"));
        Assert.Contains("That code is not right", await wrong.Content.ReadAsStringAsync());

        AssertRedirect(await admin.PostFormAsync("/account/two-factor?handler=Enable", token, ("Input.Code", Totp.Code(key))));
        var enabled = await admin.GetStringAsync("/account/two-factor");
        var recoveryCode = Html.Find(enabled, "<ul class=\"codes mono\">\\s*<li>([^<]+)</li>");

        // The setup re-issued this browser's cookie with "amr=mfa": the admin pages open now.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin")).StatusCode);

        // 3. A new login stops after the password and asks for the code.
        var phone = strict.Browser();
        var afterPassword = await PostLoginAsync(phone, AdminEmail, AdminPassword);
        Assert.StartsWith("/account/login-2fa", AssertRedirect(afterPassword));
        Assert.Equal(HttpStatusCode.Redirect, (await phone.GetAsync("/admin")).StatusCode); // not logged in yet

        token = await phone.GetFormTokenAsync("/account/login-2fa");
        AssertRedirect(await phone.PostFormAsync("/account/login-2fa", token, ("Input.Code", Totp.Code(key))));
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/admin")).StatusCode);

        // 4. Lost phone: a recovery code works instead, but only once.
        var lostPhone = strict.Browser();
        await PostLoginAsync(lostPhone, AdminEmail, AdminPassword);
        token = await lostPhone.GetFormTokenAsync("/account/login-2fa");
        AssertRedirect(await lostPhone.PostFormAsync("/account/login-2fa", token, ("Input.Code", recoveryCode)));
        Assert.Equal(HttpStatusCode.OK, (await lostPhone.GetAsync("/admin")).StatusCode);

        var again = strict.Browser();
        await PostLoginAsync(again, AdminEmail, AdminPassword);
        token = await again.GetFormTokenAsync("/account/login-2fa");
        var reused = await again.PostFormAsync("/account/login-2fa", token, ("Input.Code", recoveryCode));
        Assert.Contains("That code is not right", await reused.Content.ReadAsStringAsync());
    }

    private static async Task<string> ResetAsync(HttpClient browser, string link, string newPassword)
    {
        var page = await browser.GetStringAsync(link);
        var token = Html.Find(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        var userId = Html.Find(page, "id=\"UserId\" name=\"UserId\" value=\"([^\"]+)\"");
        var code = Html.Find(page, "id=\"Code\" name=\"Code\" value=\"([^\"]+)\"");
        var response = await browser.PostFormAsync("/account/reset-password", token,
            ("UserId", userId), ("Code", code), ("Input.Password", newPassword), ("Input.ConfirmPassword", newPassword));
        return await response.Content.ReadAsStringAsync();
    }
}

/// <summary>
/// What an authenticator app does (RFC 6238): HMAC-SHA1 of the current 30-second time step,
/// with the shared key, cut down to 6 digits. Lets a test "be the phone".
/// </summary>
public static class Totp
{
    public static string Code(string base32Key)
    {
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);

        var hash = HMACSHA1.HashData(Base32Decode(base32Key), counter);
        var offset = hash[^1] & 0x0F;
        var number = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (number % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }
}
