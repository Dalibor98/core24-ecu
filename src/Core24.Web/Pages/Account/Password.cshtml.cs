using System.ComponentModel.DataAnnotations;
using Core24.Web.Data;
using Core24.Web.Features.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Core24.Web.Pages.Account;

/// <summary>Change password while logged in. Requires login (AuthorizeFolder in Program.cs).</summary>
public class PasswordModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, Notifier notifier) : PageModel
{
    public class InputModel
    {
        [Required, DataType(DataType.Password), Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = "";

        [Required, StringLength(100, MinimumLength = 10), DataType(DataType.Password), Display(Name = "New password")]
        public string NewPassword { get; set; } = "";

        [DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "The passwords don't match."), Display(Name = "Repeat new password")]
        public string ConfirmPassword { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? Flash { get; set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();

        // Asking for the current password means a stolen, still-open session can't lock the owner out.
        var result = await users.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError("", e.Code == "PasswordMismatch" ? "The current password is wrong." : e.Description);
            return Page();
        }

        // The password change gave the user a new security stamp. Re-issue THIS browser's cookie
        // with it, otherwise the stamp check would log the user out here too.
        await signIn.RefreshSignInAsync(user);
        await notifier.PasswordChangedAsync(user.Email!, ct);

        Flash = "Password changed.";
        return RedirectToPage();
    }
}
