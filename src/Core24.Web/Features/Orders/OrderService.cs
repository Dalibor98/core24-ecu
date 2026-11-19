using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Core24.Web.Data;
using Core24.Web.Features.Catalog;
using Core24.Web.Features.Notifications;
using Core24.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Core24.Web.Features.Orders;

/// <summary>What the customer types into the order form. The files come separately.</summary>
public class NewOrderInput
{
    [Required, StringLength(100)]
    public string Name { get; set; } = "";

    [Required, EmailAddress, StringLength(254)]
    public string Email { get; set; } = "";

    [Phone, StringLength(40)]
    public string? Phone { get; set; }

    public OrderType Type { get; set; } = OrderType.OriginalFile;

    /// <summary>
    /// The vehicle and computer the customer clicked in the price list. Required for an original file.
    /// For a special request it's the vehicle, unless the customer ticked "my vehicle isn't in the list".
    /// </summary>
    public int? PriceListEntryId { get; set; }

    /// <summary>Special requests only: the vehicle isn't in the price list, so it's typed into Make and Model.</summary>
    public bool VehicleNotListed { get; set; }

    [StringLength(40)]
    public string? Make { get; set; }

    [StringLength(100), Display(Name = "Model, engine and year")]
    public string? Model { get; set; }

    /// <summary>Optional for an original file; for a special request it describes the job, so it's required.</summary>
    [StringLength(2000)]
    public string? Message { get; set; }
}

public class UploadOptions
{
    public const string Section = "Uploads";

    // Upper bound for one whole upload request (all files + form fields). Attributes need a constant.
    public const long MaxRequestBytes = 110 * 1024 * 1024;

    public int MaxFiles { get; set; } = 6;
    public long MaxFileBytes { get; set; } = 16 * 1024 * 1024;
    // No default values here on purpose: the configuration binder ADDS array items from
    // appsettings.json to whatever the array already holds, so a default of [".bin"] plus
    // the file's [".bin", ".ori"] would become [".bin", ".bin", ".ori"]. The list lives in appsettings.json.
    public string[] AllowedExtensions { get; set; } = [];

    /// <summary>
    /// false: accept a file whose name-checksum doesn't match, but warn the workshop (default).
    /// true: refuse it and ask the customer to read the ECU again.
    /// </summary>
    public bool RejectChecksumMismatch { get; set; }
}

public sealed record OrderError(string Field, string Message);

public sealed record CreateOrderResult(Order? Order, IReadOnlyList<OrderError> Errors)
{
    public bool Succeeded => Order is not null;
}

/// <summary>
/// Everything that changes an order: creating it, and the workshop's steps (start, upload, ready, cancel).
/// Payment lives in PaymentService.
/// </summary>
public class OrderService(
    AppDbContext db,
    IFileStore store,
    Notifier notifier,
    IOptions<UploadOptions> uploadOptions,
    IOptions<AppOptions> appOptions,
    TimeProvider time,
    ILogger<OrderService> log)
{
    private UploadOptions Uploads => uploadOptions.Value;

    public async Task<CreateOrderResult> CreateAsync(NewOrderInput input, IReadOnlyList<IFormFile> uploads, string? userId, CancellationToken ct = default)
    {
        // 1. Validate everything we can before touching the disk.
        var errors = ValidateFiles(uploads, "Files");

        var special = input.Type == OrderType.SpecialRequest;
        var typedVehicle = special && input.VehicleNotListed;

        // The vehicle comes from the price list (clicked, so no typos), except for a special request
        // for a vehicle that isn't in the list.
        PriceListEntry? entry = null;
        if (!typedVehicle)
        {
            entry = input.PriceListEntryId is { } id ? await db.PriceList.SingleOrDefaultAsync(p => p.Id == id && p.IsActive, ct) : null;
            if (entry is null)
                errors.Add(new("PriceListEntryId", input.PriceListEntryId is null
                    ? "Choose your vehicle and its computer."
                    : "That vehicle is no longer in our price list. Please choose again."));
        }
        else if (string.IsNullOrWhiteSpace(input.Make))
            errors.Add(new("Make", "Enter the vehicle's make."));

        if (special && string.IsNullOrWhiteSpace(input.Message))
            errors.Add(new("Message", "Describe what you need, so we can tell you the price."));

        if (errors.Count > 0) return new(null, errors);

        var now = time.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            AccessToken = NewAccessToken(),
            UserId = userId,
            Type = input.Type,
            CustomerName = input.Name.Trim(),
            CustomerEmail = input.Email.Trim(),
            CustomerEmailNormalized = input.Email.Trim().ToUpperInvariant(),
            CustomerPhone = Blank(input.Phone),
            VehicleMake = entry?.Brand ?? input.Make!.Trim(),
            VehicleModel = entry is null ? Blank(input.Model) : Cut($"{entry.Model} · {entry.Engine} · {entry.Ecu}", 250),
            CustomerNote = Blank(input.Message),
            Currency = appOptions.Value.Currency,
            CreatedAtUtc = now,
        };

        // An original file has its price up front. Copy name and price: the order keeps the price it was sold at.
        // A special request starts without a price (TotalCents 0); the workshop sets it after agreeing it by email.
        if (!special)
            order.Items.Add(new OrderItem { PriceListEntryId = entry!.Id, Name = Cut($"Original file: {entry.Description}", 300), PriceCents = entry.PriceCents });
        order.TotalCents = order.Items.Sum(i => i.PriceCents);

        // 2. Store the files. Disk and database can't share one transaction,
        //    so if anything fails after this point we delete what we wrote.
        try
        {
            foreach (var upload in uploads)
                order.Files.Add(await StoreAsync(upload, order.Id, OrderFileKind.Original, now, ct));

            if (Uploads.RejectChecksumMismatch && order.Files.Any(f => f.NameChecksumMatches == false))
            {
                DeleteStored(order.Files);
                return new(null, [new("Files", "A file's contents don't match the checksum in its name. It may be damaged. Please read the ECU again and upload the new file.")]);
            }

            // The VIN normally comes from the file name; all files of one order belong to one truck.
            order.Vin = order.Files.Select(f => f.Vin).FirstOrDefault(v => v is not null);

            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            DeleteStored(order.Files);
            throw;
        }

        log.LogInformation("Order {Reference} ({Type}) created with {Count} file(s)", order.Reference, order.Type, order.Files.Count);

        // 3. Only after the order is safely saved: tell people about it.
        await notifier.OrderSubmittedAsync(order, ct);
        return new(order, []);
    }

    public async Task<bool> StartWorkAsync(Guid orderId, CancellationToken ct = default) =>
        await MoveAsync(orderId, from: [OrderStatus.Submitted], to: OrderStatus.InProgress, ct);

    public async Task<bool> CancelAsync(Guid orderId, CancellationToken ct = default) =>
        await MoveAsync(orderId, from: [OrderStatus.Submitted, OrderStatus.InProgress, OrderStatus.Ready], to: OrderStatus.Cancelled, ct);

    /// <summary>The workshop uploads the modified file(s). Allowed until the order is marked ready.</summary>
    public async Task<IReadOnlyList<OrderError>> AddModifiedFilesAsync(Guid orderId, IReadOnlyList<IFormFile> uploads, CancellationToken ct = default)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return [new("", "Order not found.")];
        if (order.Status is not (OrderStatus.Submitted or OrderStatus.InProgress))
            return [new("", "Files can only be added before the order is marked ready.")];

        var errors = ValidateFiles(uploads, "ModifiedFiles");
        if (errors.Count > 0) return errors;

        var now = time.GetUtcNow().UtcDateTime;
        var added = new List<OrderFile>();
        try
        {
            foreach (var upload in uploads)
                added.Add(await StoreAsync(upload, order.Id, OrderFileKind.Modified, now, ct));
            db.OrderFiles.AddRange(added);
            if (order.Status == OrderStatus.Submitted) order.Status = OrderStatus.InProgress;
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            DeleteStored(added);
            throw;
        }
        return [];
    }

    public async Task<bool> RemoveModifiedFileAsync(Guid orderId, int fileId, CancellationToken ct = default)
    {
        var file = await db.OrderFiles.Include(f => f.Order)
            .SingleOrDefaultAsync(f => f.Id == fileId && f.OrderId == orderId && f.Kind == OrderFileKind.Modified, ct);
        if (file is null || file.Order!.Status is not (OrderStatus.Submitted or OrderStatus.InProgress)) return false;

        db.OrderFiles.Remove(file);
        await db.SaveChangesAsync(ct);
        store.Delete(file.StorageKey);
        return true;
    }

    /// <summary>
    /// The workshop sets the price before the file is ready: the price agreed by email for a special request,
    /// or a corrected price when the customer picked the wrong computer. Replaces whatever was there.
    /// </summary>
    public async Task<OrderError?> SetPriceAsync(Guid orderId, string? name, long priceCents, CancellationToken ct = default)
    {
        if (priceCents <= 0) return new("Price", "The price must be more than 0.");

        // Price and status are checked and changed in ONE transaction: the order can't be marked ready
        // (and the customer asked to pay) in the middle of a price change.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Orders
            .Where(o => o.Id == orderId && (o.Status == OrderStatus.Submitted || o.Status == OrderStatus.InProgress))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.TotalCents, priceCents), ct);
        if (changed == 0) return new("", "The price can only change before the order is marked ready.");

        var old = await db.OrderItems.Where(i => i.OrderId == orderId).ToListAsync(ct);
        db.OrderItems.RemoveRange(old);
        db.OrderItems.Add(new OrderItem
        {
            OrderId = orderId,
            // Keep the link to the price list when the workshop only corrects the price of a price-list line.
            PriceListEntryId = old.Count == 1 ? old[0].PriceListEntryId : null,
            Name = Cut(Blank(name) ?? (old.Count == 1 ? old[0].Name : "Special request"), 300),
            PriceCents = priceCents,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    /// <summary>Workshop is done: the customer gets the "ready, please pay" email.</summary>
    public async Task<OrderError?> MarkReadyAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.Files).SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return new("", "Order not found.");
        if (order.Status is not (OrderStatus.Submitted or OrderStatus.InProgress)) return new("", $"An order that is {order.Status} can't be marked ready.");
        if (!order.ModifiedFiles.Any()) return new("", "Upload the modified file first.");
        if (order.PriceToBeAgreed) return new("", "Set the price first (agree it with the customer by email).");

        // The checks above read the order; between that read and a save, a second click could
        // pass the same checks. The conditional UPDATE lets only one of them through, so only one email goes out.
        var now = time.GetUtcNow().UtcDateTime;
        var changed = await db.Orders
            .Where(o => o.Id == orderId && o.TotalCents > 0 && (o.Status == OrderStatus.Submitted || o.Status == OrderStatus.InProgress))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Ready).SetProperty(o => o.ReadyAtUtc, now), ct);
        if (changed == 0) return new("", "The order was already marked ready.");

        order.Status = OrderStatus.Ready;
        order.ReadyAtUtc = now;
        await notifier.OrderReadyAsync(order, ct);
        return null;
    }

    /// <summary>
    /// A status change that only happens if the order is still in one of the expected states.
    /// The check and the write are one SQL UPDATE, so two clicks at once can't both succeed.
    /// </summary>
    private async Task<bool> MoveAsync(Guid orderId, OrderStatus[] from, OrderStatus to, CancellationToken ct)
    {
        var changed = await db.Orders
            .Where(o => o.Id == orderId && from.Contains(o.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, to), ct);
        return changed == 1;
    }

    private List<OrderError> ValidateFiles(IReadOnlyList<IFormFile> uploads, string field)
    {
        var errors = new List<OrderError>();
        if (uploads.Count == 0) errors.Add(new(field, "Choose at least one file."));
        if (uploads.Count > Uploads.MaxFiles) errors.Add(new(field, $"Upload at most {Uploads.MaxFiles} files per order."));

        foreach (var file in uploads)
        {
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!Uploads.AllowedExtensions.Contains(ext))
                errors.Add(new(field, $"{file.FileName}: only {string.Join(", ", Uploads.AllowedExtensions)} files are accepted."));
            if (file.Length == 0)
                errors.Add(new(field, $"{file.FileName} is empty."));
            if (file.Length > Uploads.MaxFileBytes)
                errors.Add(new(field, $"{file.FileName} is larger than {Uploads.MaxFileBytes / 1024 / 1024} MB."));
            // The extension is just part of the name: check what the bytes really are.
            if (file.Length > 0 && FileSignature.DetectForbidden(file) is { } realType)
                errors.Add(new(field, $"{file.FileName} is {realType}, not an ECU file."));
        }
        return errors;
    }

    private async Task<OrderFile> StoreAsync(IFormFile upload, Guid orderId, OrderFileKind kind, DateTime now, CancellationToken ct)
    {
        var fileName = CleanFileName(upload.FileName);

        await using var stream = upload.OpenReadStream();
        var stored = await store.SaveAsync(stream, $"orders/{orderId:N}/{kind.ToString().ToLowerInvariant()}", ct);
        var parsed = EcuFileName.TryParse(fileName);

        return new OrderFile
        {
            OrderId = orderId,
            Kind = kind,
            FileName = fileName,
            StorageKey = stored.Key,
            SizeBytes = stored.SizeBytes,
            Md5 = stored.Md5,
            Vin = parsed?.Vin,
            EcuType = parsed?.EcuType,
            MemoryArea = parsed?.MemoryArea,
            ReadMethod = parsed?.ReadMethod,
            NameChecksumMatches = parsed is null ? null : parsed.Md5 == stored.Md5,
            UploadedAtUtc = now,
        };
    }

    private void DeleteStored(IEnumerable<OrderFile> files)
    {
        foreach (var f in files)
        {
            try { store.Delete(f.StorageKey); }
            catch (Exception ex) { log.LogWarning(ex, "Could not delete {Key}", f.StorageKey); }
        }
    }

    /// <summary>
    /// The uploader chooses the name, so treat it as hostile text: we show it in pages and emails
    /// and send it back in download headers.
    /// </summary>
    public static string CleanFileName(string raw)
    {
        // Browsers send only the name, but old ones sent "C:\Users\...\file.bin". Keep just the name.
        var name = Path.GetFileName(raw.Replace('\\', '/'));
        // Control characters (new lines, NUL...) have no place in a file name and can break headers and logs.
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        // The database column holds 255 characters. Keep the end: it holds the extension.
        if (name.Length > 200) name = name[^200..];
        return name.Length == 0 ? "file.bin" : name;
    }

    /// <summary>32 random bytes = 256 bits: impossible to guess. Base64Url keeps it safe inside a URL.</summary>
    private static string NewAccessToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Shortens text to fit its database column.</summary>
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
