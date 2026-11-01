namespace Core24.Web.Features.Payments;

public enum PaymentStatus
{
    Pending,    // checkout started, customer is on the payment page
    Succeeded,  // provider confirmed the money arrived
    Refunded,   // the money was sent back (full refund)
}

/// <summary>One checkout attempt for an order.</summary>
public class Payment
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }

    /// <summary>"stripe" or "dev" (the fake checkout used on a developer PC).</summary>
    public required string Provider { get; set; }

    /// <summary>The provider's id for this checkout (Stripe: "cs_..."). Unique, so a webhook finds exactly one row.</summary>
    public required string SessionId { get; set; }

    /// <summary>The provider's payment page. Reused if the customer clicks "Pay" twice.</summary>
    public required string CheckoutUrl { get; set; }

    public long AmountCents { get; set; }
    public required string Currency { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>The provider's id for the money itself (Stripe: the PaymentIntent "pi_..."). Needed to refund.</summary>
    public string? ProviderPaymentId { get; set; }

    /// <summary>The provider's id for the refund (Stripe: "re_..."), to find it in the dashboard.</summary>
    public string? RefundId { get; set; }
    public DateTime? RefundedAtUtc { get; set; }
}
