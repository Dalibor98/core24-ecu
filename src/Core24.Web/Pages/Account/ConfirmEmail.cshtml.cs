using System.Text;
using Core24.Web.Data;
using Core24.Web.Features.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace Core24.Web.Pages.Account;

public class ConfirmEmailModel(UserManager<AppUser> users, GuestOrderClaimer claimer) : PageModel
{
    public bool Confirmed { get; private set; }
    public int ClaimedOrders { get; private set; }

    public async Task OnGetAsync(string? userId, string? code, CancellationToken ct)
    {
        if (userId is null || code is null) return;
        var user = await users.FindByIdAsync(userId);
        if (user is null) return;

        string token;
        try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return; }

        var result = await users.ConfirmEmailAsync(user, token);
        Confirmed = result.Succeeded;
        if (Confirmed) ClaimedOrders = await claimer.ClaimAsync(user, ct);
    }
}
