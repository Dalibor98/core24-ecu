using System.ComponentModel.DataAnnotations;
using Core24.Web.Data;
using Core24.Web.Features.Accounts;
using Core24.Web.Features.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Core24.Web.Pages.Account;

public class LoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, GuestOrderClaimer claimer, Notifier notifier, IOptions<AppOptions> app) : PageModel
{
    public class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Display(Name = "Keep me logged in")]
        public bool RememberMe { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public bool NeedsConfirmation { get; private set; }
    public bool ConfirmationSent { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var result = await signIn.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            // Picks up guest orders placed with this email since the last login.
            if (await users.FindByEmailAsync(Input.Email) is { } user)
                await claimer.ClaimAsync(user, ct);

            // Only ever redirect to our own pages: ?returnUrl=https://evil.example must not work.
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/account");
        }

        if (result.IsLockedOut)
            ModelState.AddModelError("", "Too many failed attempts. Try again in a few minutes.");
        else if (result.IsNotAllowed)
            NeedsConfirmation = true;
        else
            // One message for "no such user" and "wrong password": don't reveal which emails have accounts.
            ModelState.AddModelError("", "Wrong email or password.");
        return Page();
    }

    public async Task<IActionResult> OnPostResendAsync(CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(Input.Email);
        if (user is { EmailConfirmed: false })
            await RegisterModel.SendConfirmationAsync(users, notifier, app.Value, user, ct);
        ModelState.Clear();
        ConfirmationSent = true;
        return Page();
    }
}
