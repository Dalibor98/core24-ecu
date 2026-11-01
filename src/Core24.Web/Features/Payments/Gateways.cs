using Core24.Web.Features.Orders;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Core24.Web.Features.Payments;

public sealed record CheckoutSession(string SessionId, string RedirectUrl);

/// <summary>
/// "Send the customer somewhere to pay for this order." The confirmation comes back
/// separately (Stripe: webhook; dev: the fake checkout page), never from the browser alone.
/// </summary>
public interface IPaymentGateway
{
    string Name { get; }
    Task<CheckoutSession> CreateCheckoutAsync(Order order, string successUrl, string cancelUrl, CancellationToken ct = default);

    /// <summary>Sends the whole amount of a succeeded payment back. Returns the provider's refund id.</summary>
    Task<string> RefundAsync(Payment payment, CancellationToken ct = default);
}

public class PaymentOptions
{
    public const string Section = "Payments";

    /// <summary>"Stripe" for real payments, "Dev" for the fake checkout (development only).</summary>
    public string Provider { get; set; } = "Stripe";

    public string StripeSecretKey { get; set; } = "";
    public string StripeWebhookSecret { get; set; } = "";
}

/// <summary>Stripe Checkout: Stripe hosts the payment page, so card data never touches our server.</summary>
public class StripePaymentGateway(IOptions<PaymentOptions> options) : IPaymentGateway
{
    public string Name => "stripe";

    public async Task<CheckoutSession> CreateCheckoutAsync(Order order, string successUrl, string cancelUrl, CancellationToken ct = default)
    {
        var service = new SessionService(new StripeClient(options.Value.StripeSecretKey));
        var session = await service.CreateAsync(new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            CustomerEmail = order.CustomerEmail,
            ClientReferenceId = order.Id.ToString(),
            Metadata = new Dictionary<string, string> { ["orderId"] = order.Id.ToString(), ["reference"] = order.Reference },
            // One line per mod, priced from the ORDER (the snapshot), not from the current price list.
            LineItems = order.Items.Select(i => new SessionLineItemOptions
            {
                Quantity = 1,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = order.Currency.ToLowerInvariant(),
                    UnitAmount = i.PriceCents,
                    ProductData = new SessionLineItemPriceDataProductDataOptions { Name = $"{i.ModName} (order {order.Reference})" },
                },
            }).ToList(),
        }, cancellationToken: ct);

        return new CheckoutSession(session.Id, session.Url);
    }

    public async Task<string> RefundAsync(Payment payment, CancellationToken ct = default)
    {
        var client = new StripeClient(options.Value.StripeSecretKey);

        // A refund is made against the PaymentIntent (the money), not the Checkout Session (the page).
        // We store it when the webhook arrives; for older payments, ask Stripe for it.
        var paymentIntentId = payment.ProviderPaymentId
            ?? (await new SessionService(client).GetAsync(payment.SessionId, cancellationToken: ct)).PaymentIntentId
            ?? throw new InvalidOperationException($"Stripe has no payment for session {payment.SessionId}.");

        var refund = await new RefundService(client).CreateAsync(
            new RefundCreateOptions { PaymentIntent = paymentIntentId },
            // Idempotency key: if this request is sent twice (a retry after a timeout, a double click),
            // Stripe returns the first refund instead of refunding twice.
            new RequestOptions { IdempotencyKey = $"core24-refund-{payment.Id}" },
            ct);
        return refund.Id;
    }
}

/// <summary>
/// Development stand-in for Stripe: "pays" on a local page with one button.
/// Lets you run the whole flow without a Stripe account. Refused outside Development (see Program.cs).
/// </summary>
public class DevPaymentGateway : IPaymentGateway
{
    public string Name => "dev";

    public Task<CheckoutSession> CreateCheckoutAsync(Order order, string successUrl, string cancelUrl, CancellationToken ct = default)
    {
        var id = "dev_" + Guid.NewGuid().ToString("N");
        return Task.FromResult(new CheckoutSession(id, $"/dev/checkout/{id}"));
    }

    public Task<string> RefundAsync(Payment payment, CancellationToken ct = default) =>
        Task.FromResult("dev_refund_" + Guid.NewGuid().ToString("N"));
}
