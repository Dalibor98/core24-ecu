using System.ComponentModel.DataAnnotations;
using System.Text;
using Core24.Web.Data;
using Core24.Web.Features.Accounts;
using Core24.Web.Features.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace Core24.Web.Pages.Account;

/// <summary>Step 2 of a password reset: the page the emailed link opens.</summary>
public class ResetPasswordModel(UserManager<AppUser> users, GuestOrderClaimer claimer, Notifier notifier) : PageModel
{
    public class InputModel
    {
        [Required, StringLength(100, MinimumLength = 10), DataType(DataType.Password), Display(Name = "New password")]
        public string Password { get; set; } = "";

        [DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords don't match."), Display(Name = "Repeat new password")]
        public string ConfirmPassword { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    // Come from the link's query string, and are posted back as hidden fields.
    [BindProperty(SupportsGet = true)]
    public string? UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    public bool Done { get; private set; }

    public IActionResult OnGet()
    {
        // A secret is in the URL: don't let it leak through the Referer header or a cache.
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!ModelState.IsValid) return Page();

        var user = UserId is null ? null : await users.FindByIdAsync(UserId);
        var token = Decode(Code);
        if (user is null || token is null)
        {
            ModelState.AddModelError("", "This reset link is invalid or has expired. Ask for a new one.");
            return Page();
        }

        // Checks the token, saves the new password hash and changes the security stamp:
        // the link can't be used twice, and other logged-in browsers are logged out (within 30 minutes).
        var result = await users.ResetPasswordAsync(user, token, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError("", e.Code == "InvalidToken" ? "This reset link is invalid or has expired. Ask for a new one." : e.Description);
            return Page();
        }

        // Someone who forgot their password after five wrong tries is locked out: let them in now.
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);

        // The link arrived in the user's mailbox, so following it proves they own the address,
        // exactly like the confirmation link does. Treat it as a confirmation.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            await claimer.ClaimAsync(user, ct);
        }

        await notifier.PasswordChangedAsync(user.Email!, ct);
        Done = true;
        return Page();
    }

    private static string? Decode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return null; }
    }
}
