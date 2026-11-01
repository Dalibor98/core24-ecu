using Core24.Web.Data;
using Core24.Web.Features.Notifications;
using Core24.Web.Features.Orders;
using Core24.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Core24.Web.Features.Maintenance;

public class MaintenanceOptions
{
    public const string Section = "Maintenance";

    /// <summary>false: the background job doesn't run (the tests call <see cref="Maintenance"/> directly).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the job wakes up.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Files of paid, refunded or cancelled orders are deleted this many days later. Must match the privacy policy.</summary>
    public int FileRetentionDays { get; set; } = 90;

    /// <summary>Days after "ready" when the first and the second payment reminder go out.</summary>
    public int FirstReminderDays { get; set; } = 3;
    public int SecondReminderDays { get; set; } = 7;
}

/// <summary>
/// The jobs themselves, as an ordinary scoped service: easy to call from a test with a fake clock.
/// <see cref="MaintenanceService"/> only decides WHEN they run.
/// </summary>
public class Maintenance(
    AppDbContext db,
    IFileStore store,
    Notifier notifier,
    IOptions<MaintenanceOptions> options,
    TimeProvider time,
    ILogger<Maintenance> log)
{
    private MaintenanceOptions Options => options.Value;

    public async Task RunAsync(CancellationToken ct = default)
    {
        await SendPaymentRemindersAsync(ct);
        await DeleteExpiredFilesAsync(ct);
    }

    /// <summary>Reminder 1 after 3 days, reminder 2 after 7 days, then nothing more. Returns how many were sent.</summary>
    public async Task<int> SendPaymentRemindersAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var sent = 0;
        sent += await SendReminderAsync(alreadySent: 0, readyBefore: now.AddDays(-Options.FirstReminderDays), ct);
        sent += await SendReminderAsync(alreadySent: 1, readyBefore: now.AddDays(-Options.SecondReminderDays), ct);
        return sent;
    }

    private async Task<int> SendReminderAsync(int alreadySent, DateTime readyBefore, CancellationToken ct)
    {
        var due = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Ready && o.PaymentRemindersSent == alreadySent && o.ReadyAtUtc < readyBefore)
            .Include(o => o.Items)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var order in due)
        {
            // Claim the order first ("set 1 WHERE still 0"): if the job ever ran twice at once,
            // only one run would get the row, so the customer never gets the same reminder twice.
            var claimed = await db.Orders
                .Where(o => o.Id == order.Id && o.PaymentRemindersSent == alreadySent && o.Status == OrderStatus.Ready)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentRemindersSent, alreadySent + 1), ct);
            if (claimed == 0) continue;

            await notifier.PaymentReminderAsync(order, ct);
            log.LogInformation("Order {Reference}: payment reminder {Number} sent", order.Reference, alreadySent + 1);
            sent++;
        }
        return sent;
    }

    /// <summary>
    /// Data minimisation (GDPR): ECU files contain the VIN, so we don't keep them forever.
    /// Deletes the bytes of finished orders after the retention period. The rows stay
    /// (name, size, MD5) as a record of what was delivered. Returns how many orders were cleaned.
    /// </summary>
    public async Task<int> DeleteExpiredFilesAsync(CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-Options.FileRetentionDays);

        // Paid and refunded orders count from the payment; cancelled ones from when they were created
        // (we don't record when an order was cancelled).
        var expired = await db.Orders
            .Where(o => o.FilesDeletedAtUtc == null && (
                ((o.Status == OrderStatus.Paid || o.Status == OrderStatus.Refunded) && o.PaidAtUtc < cutoff) ||
                (o.Status == OrderStatus.Cancelled && o.CreatedAtUtc < cutoff)))
            .Include(o => o.Files)
            .Take(100) // a batch per run keeps each run short; the next run takes the rest
            .ToListAsync(ct);

        foreach (var order in expired)
        {
            var allDeleted = true;
            foreach (var file in order.Files)
            {
                try { store.Delete(file.StorageKey); }
                catch (Exception ex)
                {
                    log.LogError(ex, "Order {Reference}: could not delete {Key}", order.Reference, file.StorageKey);
                    allDeleted = false;
                }
            }

            // If anything failed, FilesDeletedAtUtc stays empty and the next run tries this order again.
            if (!allDeleted) continue;
            order.FilesDeletedAtUtc = time.GetUtcNow().UtcDateTime;
            log.LogInformation("Order {Reference}: {Count} file(s) deleted after {Days} days", order.Reference, order.Files.Count, Options.FileRetentionDays);
        }

        await db.SaveChangesAsync(ct);
        return expired.Count(o => o.FilesDeletedAtUtc is not null);
    }
}

/// <summary>
/// Runs <see cref="Maintenance"/> every hour, for as long as the app runs.
/// A BackgroundService is a singleton, but the DbContext is scoped, so each run creates its own scope.
/// </summary>
public class MaintenanceService(IServiceScopeFactory scopes, IOptions<MaintenanceOptions> options, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        // Let the app finish starting (and migrating the database) before the first run.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<Maintenance>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one bad run stop the job for good: an unhandled exception here would end the loop.
                log.LogError(ex, "Maintenance run failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
