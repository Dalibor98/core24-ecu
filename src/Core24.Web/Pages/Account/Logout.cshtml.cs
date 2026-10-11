using Core24.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Core24.Web.Pages.Account;

/// <summary>
/// Logging out happens only on POST, so a link or image on another site can't log people out.
/// A GET (e.g. from the landing page's menu, which has no anti-forgery token) shows a confirm button.
/// </summary>
public class LogoutModel(SignInManager<AppUser> signIn) : PageModel
{
    public IActionResult OnGet() => signIn.IsSignedIn(User) ? Page() : Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync();
        return Redirect("/");
    }
}
