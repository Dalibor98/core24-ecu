using System.ComponentModel.DataAnnotations;
using Core24.Web.Data;
using Core24.Web.Features.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Core24.Web.Pages.Account;

/// <summary>
/// The second step of logging in, for accounts with two-factor login on. After the right password,
/// Identity sets a short-lived "two-factor" cookie (not a login yet) and the login page sends the user here.
/// </summary>
public class LoginTwoFactorModel(SignInManager<AppUser> signIn, GuestOrderClaimer claimer) : PageModel
{
    public class InputModel
    {
        [Required, StringLength(20), Display(Name = "Code from your app, or a recovery code")]
        public string Code { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool RememberMe { get; set; }

    public async Task<IActionResult> OnGetAsync() =>
        // No two-factor cookie: the password step was skipped or took too long. Start again.
        await signIn.GetTwoFactorAuthenticationUserAsync() is null ? RedirectToPage("/Account/Login") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return RedirectToPage("/Account/Login");
        if (!ModelState.IsValid) return Page();

        // App codes are 6 digits ("123 456" is fine too). Anything else is tried as a recovery code.
        var digits = Input.Code.Replace(" ", "").Replace("-", "");
        var result = digits.Length == 6 && digits.All(char.IsAsciiDigit)
            ? await signIn.TwoFactorAuthenticatorSignInAsync(digits, RememberMe, rememberClient: false)
            : await signIn.TwoFactorRecoveryCodeSignInAsync(Input.Code.Replace(" ", ""));

        if (result.Succeeded)
        {
            await claimer.ClaimAsync(user, ct);
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/account");
        }

        // Wrong codes count towards the same lockout as wrong passwords.
        ModelState.AddModelError("", result.IsLockedOut ? "Too many failed attempts. Try again in a few minutes." : "That code is not right.");
        return Page();
    }
}
