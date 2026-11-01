using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Core24.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QRCoder;

namespace Core24.Web.Pages.Account;

/// <summary>
/// Turn two-factor login on or off with an authenticator app (Google Authenticator, Microsoft
/// Authenticator, 1Password...). Open to every account; required for the workshop's admin login.
/// </summary>
public class TwoFactorModel(UserManager<AppUser> users, SignInManager<AppUser> signIn) : PageModel
{
    private const string Issuer = "CORE24";

    public class CodeInput
    {
        [Required, StringLength(7, MinimumLength = 6), Display(Name = "6-digit code from the app")]
        public string Code { get; set; } = "";
    }

    [BindProperty]
    public CodeInput Input { get; set; } = new();

    public bool Enabled { get; private set; }
    public int RecoveryCodesLeft { get; private set; }

    /// <summary>The secret, grouped in fours for typing by hand ("ABCD EFGH ...").</summary>
    public string SharedKey { get; private set; } = "";

    /// <summary>The same secret as a QR code (an SVG image as a data: URL).</summary>
    public string QrCodeDataUrl { get; private set; } = "";

    /// <summary>Shown exactly once, right after they are created. TempData survives one redirect.</summary>
    [TempData]
    public string? NewRecoveryCodes { get; set; }

    [TempData]
    public string? Flash { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        await LoadAsync(user);
        return Page();
    }

    /// <summary>Step 2 of setup: the user scanned the QR code and types the code the app shows.</summary>
    public async Task<IActionResult> OnPostEnableAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();

        // Proves the app and the server share the same secret before we start requiring it.
        // Without this check, a mistyped key would lock the user out on the next login.
        if (!ModelState.IsValid || !await VerifyAsync(user, Input.Code))
        {
            ModelState.AddModelError("", "That code is not right. Check the time on your phone and try the newest code.");
            await LoadAsync(user);
            return Page();
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        NewRecoveryCodes = string.Join(' ', await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) ?? []);

        // This browser logged in with the password only. Re-issue its cookie with the same
        // "amr=mfa" claim a two-factor login would add, so the admin pages open without logging in again.
        await signIn.SignInWithClaimsAsync(user, isPersistent: false, [new Claim("amr", "mfa")]);

        Flash = "Two-factor login is on.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRecoveryCodesAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!user.TwoFactorEnabled) return RedirectToPage();

        // New codes replace the old ones: the old list stops working.
        NewRecoveryCodes = string.Join(' ', await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) ?? []);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();

        // Turning protection OFF needs proof too, or a stolen session could quietly remove it.
        if (!ModelState.IsValid || !await VerifyAsync(user, Input.Code))
        {
            ModelState.AddModelError("", "That code is not right.");
            await LoadAsync(user);
            return Page();
        }

        await users.SetTwoFactorEnabledAsync(user, false);
        // A new secret, so the old app entry can never work again if 2FA is turned back on.
        await users.ResetAuthenticatorKeyAsync(user);
        await signIn.RefreshSignInAsync(user);

        Flash = "Two-factor login is off.";
        return RedirectToPage();
    }

    private async Task<bool> VerifyAsync(AppUser user, string code)
    {
        var clean = code.Replace(" ", "").Replace("-", "");
        return await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, clean);
    }

    private async Task LoadAsync(AppUser user)
    {
        Enabled = user.TwoFactorEnabled;
        if (Enabled)
        {
            RecoveryCodesLeft = await users.CountRecoveryCodesAsync(user);
            return;
        }

        // The secret is created once and stored with the user (AspNetUserTokens table).
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }

        SharedKey = string.Join(' ', key!.Chunk(4).Select(c => new string(c)));

        // The standard format every authenticator app understands.
        var uri = $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var svg = new SvgQRCode(data).GetGraphic(5);
        QrCodeDataUrl = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
    }
}
