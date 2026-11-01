using Core24.Web.Data;
using Core24.Web.Features.Payments;

namespace Core24.Web.Features.Orders;

public enum OrderStatus
{
    Submitted,   // customer sent the file
    InProgress,  // workshop started working on it
    Ready,       // modified file uploaded, waiting for payment
    Paid,        // payment confirmed, download unlocked
    Cancelled,
    Refunded,    // was paid, the money went back to the customer; download locked again
}

public class Order
{
    // Version 7 GUIDs start with a timestamp, so new rows land at the end of the index.
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Random secret in the customer's private link (/o/{token}). Treat it like a password.</summary>
    public required string AccessToken { get; set; }

    /// <summary>Null for guest orders. Set when a logged-in customer orders, or when a guest claims the order later.</summary>
    public string? UserId { get; set; }
    public AppUser? User { get; set; }

    public required string CustomerName { get; set; }
    public required string CustomerEmail { get; set; }

    /// <summary>Trimmed, upper-case email, the same form ASP.NET Identity uses. Used to match guest orders to accounts.</summary>
    public required string CustomerEmailNormalized { get; set; }
    public string? CustomerPhone { get; set; }

    public required string VehicleMake { get; set; }
    public string? VehicleModel { get; set; }
    public string? Vin { get; set; }
    public string? CustomerNote { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Submitted;

    /// <summary>Sum of the item prices at the moment of ordering, in cents.</summary>
    public long TotalCents { get; set; }
    public required string Currency { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadyAtUtc { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public int DownloadCount { get; set; }

    /// <summary>"You haven't paid yet" emails sent so far (0, 1 or 2). See Maintenance.</summary>
    public int PaymentRemindersSent { get; set; }

    /// <summary>Set when the retention job deleted this order's files from disk. The rows stay, as a record.</summary>
    public DateTime? FilesDeletedAtUtc { get; set; }

    public List<OrderItem> Items { get; set; } = [];
    public List<OrderFile> Files { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];

    /// <summary>
    /// Short code for humans ("A3F09B12"). Taken from the END of the GUID: the start of a
    /// version 7 GUID is a timestamp, so two orders a minute apart would share it.
    /// </summary>
    public string Reference => Id.ToString("N")[^8..].ToUpperInvariant();

    public IEnumerable<OrderFile> OriginalFiles => Files.Where(f => f.Kind == OrderFileKind.Original);
    public IEnumerable<OrderFile> ModifiedFiles => Files.Where(f => f.Kind == OrderFileKind.Modified);
}

/// <summary>
/// One chosen mod. Name and price are COPIED from the price list, so changing the
/// price list later never changes what an old order costs.
/// </summary>
public class OrderItem
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public int ModId { get; set; }
    public required string ModName { get; set; }
    public long PriceCents { get; set; }
}

public enum OrderFileKind
{
    Original,  // uploaded by the customer
    Modified,  // uploaded by the workshop
}

/// <summary>
/// A file belonging to an order. The bytes live in file storage; the database only
/// keeps where they are and what we know about them.
/// </summary>
public class OrderFile
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public OrderFileKind Kind { get; set; }

    /// <summary>The name the file had on the uploader's PC. Shown to people, never used as a path.</summary>
    public required string FileName { get; set; }

    /// <summary>Where the file storage keeps the bytes (e.g. "orders/{id}/{guid}.bin").</summary>
    public required string StorageKey { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>MD5 of the bytes we actually received, upper-case hex.</summary>
    public required string Md5 { get; set; }

    // Read from the file name when it follows the VIN_ECU_MEMORY_MD5_METHOD pattern.
    public string? Vin { get; set; }
    public string? EcuType { get; set; }
    public string? MemoryArea { get; set; }
    public string? ReadMethod { get; set; }

    /// <summary>Null: the name had no checksum. True/false: the checksum in the name matched the bytes or not.</summary>
    public bool? NameChecksumMatches { get; set; }

    public DateTime UploadedAtUtc { get; set; }
}
