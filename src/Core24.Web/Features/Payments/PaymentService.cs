using Core24.Web.Data;
using Core24.Web.Features.Notifications;
using Core24.Web.Features.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Core24.Web.Features.Payments;

public enum CompleteResult
{
    Completed,          // first time we hear about it: order is now Paid
    AlreadyProcessed,   // a repeat delivery of the same event: nothing to do
    UnknownSession,     // not one of our checkouts
    AmountMismatch,     // paid amount differs from what we asked for: needs a human
}

public class PaymentService(
    AppDbContext db,
    IPaymentGateway gateway,
    Notifier notifier,
    IOptions<AppOptions> appOptions,
    TimeProvider time,
    ILogger<PaymentService> log)
{
    // Stripe Checkout sessions expire after 24 hours; reuse a pending one younger than that.
    private static readonly TimeSpan ReuseWindow = TimeSpan.FromHours(23);

    /// <summary>Returns the URL to send the customer to, or null if the order can't be paid now.</summary>
    public async Task<string?> StartCheckoutAsync(string accessToken, CancellationToken ct = default)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.Payments)
            .SingleOrDefaultAsync(o => o.AccessToken == accessToken, ct);
        if (order is not { Status: OrderStatus.Ready }) return null;

        var now = time.GetUtcNow().UtcDateTime;

        // Clicking "Pay" twice must not create two payment pages (and risk two payments).
        var pending = order.Payments
            .Where(p => p.Status == PaymentStatus.Pending && p.Provider == gateway.Name && now - p.CreatedAtUtc < ReuseWindow)
            .MaxBy(p => p.CreatedAtUtc);
        if (pending is not null) return pending.CheckoutUrl;

        var app = appOptions.Value;
        var session = await gateway.CreateCheckoutAsync(order,
            successUrl: app.Link($"/o/{order.AccessToken}?paid=1"),
            cancelUrl: app.Link($"/o/{order.AccessToken}"), ct);

        db.Payments.Add(new Payment
        {
            OrderId = order.Id,
            Provider = gateway.Name,
            SessionId = session.SessionId,
            CheckoutUrl = session.RedirectUrl,
            AmountCents = order.TotalCents,
            Currency = order.Currency,
            CreatedAtUtc = now,
        });
        await db.SaveChangesAsync(ct);
        return session.RedirectUrl;
    }

    /// <summary>
    /// Called when the provider says a checkout was paid. Providers deliver the same event
    /// more than once, sometimes at the same moment, so this must be safe to run twice:
    /// the conditional UPDATE lets exactly one call through.
    /// </summary>
    public async Task<CompleteResult> CompleteAsync(string sessionId, long amountPaidCents, string currency, string? providerPaymentId = null, CancellationToken ct = default)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.SessionId == sessionId, ct);
        if (payment is null)
        {
            log.LogWarning("Payment confirmation for unknown session {SessionId}", sessionId);
            return CompleteResult.UnknownSession;
        }

        if (payment.AmountCents != amountPaidCents || !string.Equals(payment.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            log.LogError("Session {SessionId}: expected {Expected} {Currency}, got {Paid} {PaidCurrency}",
                sessionId, payment.AmountCents, payment.Currency, amountPaidCents, currency);
            return CompleteResult.AmountMismatch;
        }

        var now = time.GetUtcNow().UtcDateTime;

        // "Set Succeeded WHERE still Pending". If another call got here first, 0 rows change.
        var claimed = await db.Payments
            .Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PaymentStatus.Succeeded)
                .SetProperty(p => p.CompletedAtUtc, now)
                .SetProperty(p => p.ProviderPaymentId, providerPaymentId), ct);
        if (claimed == 0) return CompleteResult.AlreadyProcessed;

        var paid = await db.Orders
            .Where(o => o.Id == payment.OrderId && o.Status == OrderStatus.Ready)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatus.Paid)
                .SetProperty(o => o.PaidAtUtc, now), ct);
        if (paid == 0)
        {
            // E.g. the order was cancelled, or paid through a second checkout. Money arrived anyway: a human must refund.
            log.LogError("Payment {SessionId} succeeded but order {OrderId} was not waiting for payment. Check for a refund.", sessionId, payment.OrderId);
            return CompleteResult.Completed;
        }

        var order = await db.Orders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Id == payment.OrderId, ct);
        log.LogInformation("Order {Reference} paid", order.Reference);
        await notifier.OrderPaidAsync(order, ct);
        return CompleteResult.Completed;
    }

    /// <summary>
    /// The workshop gives the money back (whole amount). A paid order becomes Refunded and its
    /// download locks again. Also used when money arrived for an order that was already cancelled.
    /// Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> RefundAsync(Guid orderId, int paymentId, CancellationToken ct = default)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == paymentId && p.OrderId == orderId, ct);
        if (payment is not { Status: PaymentStatus.Succeeded }) return "Only a successful payment can be refunded.";
        if (payment.Provider != gateway.Name) return $"This payment was made through '{payment.Provider}', but the app now uses '{gateway.Name}'. Refund it in that provider's dashboard.";

        // Money moves first. If this throws, nothing in our database changed and the admin can retry;
        // the idempotency key makes sure a retry can't refund twice.
        string refundId;
        try
        {
            refundId = await gateway.RefundAsync(payment, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Refund of payment {PaymentId} for order {OrderId} failed", paymentId, orderId);
            return "The payment provider refused the refund: " + ex.Message;
        }

        var now = time.GetUtcNow().UtcDateTime;
        await db.Payments.Where(p => p.Id == paymentId && p.Status == PaymentStatus.Succeeded)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PaymentStatus.Refunded)
                .SetProperty(p => p.RefundId, refundId)
                .SetProperty(p => p.RefundedAtUtc, now), ct);
        await db.Orders.Where(o => o.Id == orderId && o.Status == OrderStatus.Paid)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Refunded), ct);

        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, ct);
        log.LogInformation("Order {Reference}: payment {PaymentId} refunded ({RefundId})", order.Reference, paymentId, refundId);
        await notifier.OrderRefundedAsync(order, payment.AmountCents, payment.Currency, ct);
        return null;
    }
}
