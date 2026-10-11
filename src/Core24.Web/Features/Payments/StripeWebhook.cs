using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Core24.Web.Features.Payments;

public static class StripeWebhook
{
    /// <summary>
    /// Stripe calls this URL (server to server) when something happens to a payment.
    /// Configure it in the Stripe dashboard → Developers → Webhooks, events: checkout.session.completed
    /// and checkout.session.async_payment_succeeded.
    /// </summary>
    public static void MapStripeWebhook(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/payments/stripe/webhook", HandleAsync);

    private static async Task<IResult> HandleAsync(
        HttpRequest request, IOptions<PaymentOptions> options, PaymentService payments, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(StripeWebhook));
        var secret = options.Value.StripeWebhookSecret;
        if (string.IsNullOrEmpty(secret)) return Results.NotFound();

        // Read the RAW body: the signature is computed over the exact bytes Stripe sent.
        var json = await new StreamReader(request.Body).ReadToEndAsync(ct);

        Event stripeEvent;
        try
        {
            // Proves the request really comes from Stripe (HMAC with the shared webhook secret)
            // and isn't an old one replayed (timestamp within 5 minutes).
            stripeEvent = EventUtility.ConstructEvent(json, request.Headers["Stripe-Signature"], secret, throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            log.LogWarning(ex, "Rejected a webhook with an invalid signature");
            return Results.BadRequest();
        }

        // Card payments arrive as "completed" + paid. Slower methods (bank transfer) arrive later as "async_payment_succeeded".
        if (stripeEvent.Type is EventTypes.CheckoutSessionCompleted or EventTypes.CheckoutSessionAsyncPaymentSucceeded
            && stripeEvent.Data.Object is Session { PaymentStatus: "paid" } session)
        {
            var result = await payments.CompleteAsync(session.Id, session.AmountTotal ?? 0, session.Currency ?? "", ct);
            log.LogInformation("Stripe event {EventId} ({Type}): {Result}", stripeEvent.Id, stripeEvent.Type, result);
        }

        // Always 200 for events we understood or ignore; otherwise Stripe keeps retrying for days.
        return Results.Ok();
    }
}
