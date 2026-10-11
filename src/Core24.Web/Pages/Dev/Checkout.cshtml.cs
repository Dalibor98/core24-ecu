using Core24.Web.Data;
using Core24.Web.Features.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Pages.Dev;

/// <summary>
/// Plays the part of Stripe's payment page on a developer PC. "Pay" calls the same
/// PaymentService.CompleteAsync that the real Stripe webhook calls, so the rest of the flow is identical.
/// Answers 404 unless the Dev gateway is configured (and that is refused outside Development).
/// </summary>
public class CheckoutModel(AppDbContext db, IPaymentGateway gateway, PaymentService payments) : PageModel
{
    public Payment Payment { get; private set; } = null!;
    public string OrderToken { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string sessionId, CancellationToken ct) =>
        await LoadAsync(sessionId, ct) ? Page() : NotFound();

    public async Task<IActionResult> OnPostPayAsync(string sessionId, CancellationToken ct)
    {
        if (!await LoadAsync(sessionId, ct)) return NotFound();
        await payments.CompleteAsync(Payment.SessionId, Payment.AmountCents, Payment.Currency, ct);
        return Redirect($"/o/{OrderToken}?paid=1");
    }

    private async Task<bool> LoadAsync(string sessionId, CancellationToken ct)
    {
        if (gateway is not DevPaymentGateway) return false;
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.SessionId == sessionId, ct);
        if (payment is null) return false;
        Payment = payment;
        OrderToken = await db.Orders.Where(o => o.Id == payment.OrderId).Select(o => o.AccessToken).SingleAsync(ct);
        return true;
    }
}
