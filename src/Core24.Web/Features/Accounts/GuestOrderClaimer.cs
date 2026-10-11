using Core24.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Features.Accounts;

/// <summary>
/// Guests can order without an account. When someone later proves they own the same email
/// (by confirming it), their earlier guest orders move into their account.
/// Safe because those orders' links were only ever sent to that mailbox.
/// </summary>
public class GuestOrderClaimer(AppDbContext db, ILogger<GuestOrderClaimer> log)
{
    public async Task<int> ClaimAsync(AppUser user, CancellationToken ct = default)
    {
        // Never claim on an unconfirmed address: anyone can TYPE someone else's email.
        if (!user.EmailConfirmed || user.NormalizedEmail is null) return 0;

        var claimed = await db.Orders
            .Where(o => o.UserId == null && o.CustomerEmailNormalized == user.NormalizedEmail)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.UserId, user.Id), ct);

        if (claimed > 0) log.LogInformation("User {UserId} claimed {Count} guest order(s)", user.Id, claimed);
        return claimed;
    }
}
