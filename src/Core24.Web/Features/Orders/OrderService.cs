using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Core24.Web.Data;
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

    [Required, StringLength(40)]
    public string Make { get; set; } = "";

    [StringLength(100), Display(Name = "Model and year")]
    public string? Model { get; set; }

    [StringLength(2000)]
    public string? Message { get; set; }

    public List<int> ModIds { get; set; } = [];
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

        var mods = await db.Mods.Where(m => input.ModIds.Contains(m.Id) && m.IsActive).OrderBy(m => m.SortOrder).ToListAsync(ct);
        if (mods.Count == 0)
            errors.Add(new("ModIds", "Choose at least one modification."));
        else if (mods.Count != input.ModIds.Distinct().Count())
            errors.Add(new("ModIds", "One of the chosen modifications is no longer offered. Please choose again."));

        if (errors.Count > 0) return new(null, errors);

        var now = time.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            AccessToken = NewAccessToken(),
            UserId = userId,
            CustomerName = input.Name.Trim(),
            CustomerEmail = input.Email.Trim(),
            CustomerEmailNormalized = input.Email.Trim().ToUpperInvariant(),
            CustomerPhone = Blank(input.Phone),
            VehicleMake = input.Make.Trim(),
            VehicleModel = Blank(input.Model),
            CustomerNote = Blank(input.Message),
            Currency = appOptions.Value.Currency,
            CreatedAtUtc = now,
            // Copy name and price: the order keeps the price it was sold at.
            Items = mods.Select(m => new OrderItem { ModId = m.Id, ModName = m.Name, PriceCents = m.PriceCents }).ToList(),
        };
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

        log.LogInformation("Order {Reference} created with {Count} file(s)", order.Reference, order.Files.Count);

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

    /// <summary>Workshop is done: the customer gets the "ready, please pay" email.</summary>
    public async Task<OrderError?> MarkReadyAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.Files).SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return new("", "Order not found.");
        if (order.Status is not (OrderStatus.Submitted or OrderStatus.InProgress)) return new("", $"An order that is {order.Status} can't be marked ready.");
        if (!order.ModifiedFiles.Any()) return new("", "Upload the modified file first.");

        // The checks above read the order; between that read and a save, a second click could
        // pass the same checks. The conditional UPDATE lets only one of them through, so only one email goes out.
        var now = time.GetUtcNow().UtcDateTime;
        var changed = await db.Orders
            .Where(o => o.Id == orderId && (o.Status == OrderStatus.Submitted || o.Status == OrderStatus.InProgress))
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
        }
        return errors;
    }

    private async Task<OrderFile> StoreAsync(IFormFile upload, Guid orderId, OrderFileKind kind, DateTime now, CancellationToken ct)
    {
        // Browsers send only the name, but old ones sent "C:\Users\...\file.bin". Keep just the name.
        var fileName = Path.GetFileName(upload.FileName.Replace('\\', '/'));

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

    /// <summary>32 random bytes = 256 bits: impossible to guess. Base64Url keeps it safe inside a URL.</summary>
    private static string NewAccessToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
