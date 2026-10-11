using System.ComponentModel.DataAnnotations;
using System.Text;
using Core24.Web.Data;
using Core24.Web.Features.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Core24.Web.Pages.Account;

public class RegisterModel(UserManager<AppUser> users, Notifier notifier, IOptions<AppOptions> app) : PageModel
{
    public class InputModel
    {
        [Required, EmailAddress, StringLength(254)]
        public string Email { get; set; } = "";

        [Required, StringLength(100, MinimumLength = 10), DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords don't match."), Display(Name = "Repeat password")]
        public string ConfirmPassword { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool Sent { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        // The email already has an account. Answer exactly as for a new registration, so this form
        // can't be used to find out who has an account. Never change the existing password here:
        // whoever is typing may not own the mailbox. Instead the mailbox owner gets the right email.
        if (await users.FindByEmailAsync(Input.Email) is { } existing)
        {
            if (existing.EmailConfirmed)
                await notifier.AccountExistsAsync(existing.Email!, app.Value.Link("/account/login"), ct);
            else
                await SendConfirmationAsync(users, notifier, app.Value, existing, ct);
            Sent = true;
            return Page();
        }

        var user = new AppUser { UserName = Input.Email, Email = Input.Email };
        var result = await users.CreateAsync(user, Input.Password); // stores a hash, never the password
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
            return Page();
        }

        await SendConfirmationAsync(users, notifier, app.Value, user, ct);
        Sent = true;
        return Page();
    }

    /// <summary>Shared with the login page's "send the link again" button.</summary>
    public static async Task SendConfirmationAsync(UserManager<AppUser> users, Notifier notifier, AppOptions app, AppUser user, CancellationToken ct)
    {
        // Identity makes a signed, single-purpose token. Base64Url keeps its "+" and "/" intact inside a URL.
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = app.Link($"/account/confirm-email?userId={Uri.EscapeDataString(user.Id)}&code={code}");
        await notifier.ConfirmEmailAsync(user.Email!, link, ct);
    }
}
