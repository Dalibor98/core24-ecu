using System.ComponentModel.DataAnnotations;
using System.Text;
using Core24.Web.Data;
using Core24.Web.Features.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Core24.Web.Pages.Account;

/// <summary>
/// Step 1 of a password reset: "email me a link". Every answer is the same, whether the
/// address has an account or not, so this form can't be used to find out who is a customer.
/// </summary>
[EnableRateLimiting("emails")] // each POST can send an email: don't let anyone flood a mailbox
public class ForgotPasswordModel(UserManager<AppUser> users, Notifier notifier, IOptions<AppOptions> app) : PageModel
{
    public class InputModel
    {
        [Required, EmailAddress, StringLength(254)]
        public string Email { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool Sent { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        if (await users.FindByEmailAsync(Input.Email) is { } user)
        {
            // Same idea as the confirmation link: a signed, single-purpose token from Identity.
            // It contains the user's "security stamp", which changes when the password changes,
            // so a used link stops working.
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var link = app.Value.Link($"/account/reset-password?userId={Uri.EscapeDataString(user.Id)}&code={code}");
            await notifier.PasswordResetAsync(user.Email!, link, ct);
        }

        Sent = true;
        return Page();
    }
}
