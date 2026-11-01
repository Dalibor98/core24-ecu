using System.Globalization;
using System.Security.Cryptography;
using Core24.Web.Data;
using Core24.Web.Features.Orders;
using Core24.Web.Features.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Features.Downloads;

/// <summary>
/// Creates and checks short-lived download "tickets". A ticket is the file id plus an expiry time,
/// encrypted and signed by ASP.NET Data Protection: nobody can forge one or extend it, and we
/// don't need a database table to remember them.
/// </summary>
public class DownloadLinks(IDataProtectionProvider protection)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    // The "purpose" string isolates these tickets from every other use of Data Protection
    // (login cookies, Identity tokens...). A ticket can't be replayed as anything else.
    private readonly ITimeLimitedDataProtector protector =
        protection.CreateProtector("Core24.Downloads.v1").ToTimeLimitedDataProtector();

    public string CreateUrl(int fileId) =>
        "/download/" + protector.Protect(fileId.ToString(CultureInfo.InvariantCulture), Lifetime);

    public bool TryRead(string ticket, out int fileId)
    {
        fileId = 0;
        try
        {
            return int.TryParse(protector.Unprotect(ticket), CultureInfo.InvariantCulture, out fileId);
        }
        catch (CryptographicException)
        {
            return false; // expired, tampered with, or not a ticket at all
        }
    }
}

public static class DownloadEndpoints
{
    public static void MapDownloads(this IEndpointRouteBuilder app)
    {
        // Customer: the modified file, only with a valid ticket and only once the order is paid.
        app.MapGet("/download/{ticket}", async (string ticket, DownloadLinks links, AppDbContext db, IFileStore store, CancellationToken ct) =>
        {
            if (!links.TryRead(ticket, out var fileId))
                return Results.Problem("This download link has expired. Open your order page again for a fresh one.", statusCode: StatusCodes.Status410Gone);

            var file = await db.OrderFiles.AsNoTracking().Include(f => f.Order).SingleOrDefaultAsync(f => f.Id == fileId, ct);

            // Checked again here, not only when the link was made: defence in depth.
            if (file is not { Kind: OrderFileKind.Modified, Order.Status: OrderStatus.Paid })
                return Results.NotFound();

            // The retention job may have deleted the bytes (the database row stays as a record).
            var path = store.GetFullPath(file.StorageKey);
            if (!File.Exists(path))
                return Results.Problem("This file was deleted after the retention period.", statusCode: StatusCodes.Status410Gone);

            await db.Orders.Where(o => o.Id == file.OrderId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.DownloadCount, o => o.DownloadCount + 1), ct);

            // Streams from disk (no loading into memory); range support lets broken downloads resume.
            return Results.File(path, "application/octet-stream", file.FileName, enableRangeProcessing: true);
        });

        // Workshop: any file of any order, for logged-in admins only.
        app.MapGet("/admin/files/{id:int}", async (int id, AppDbContext db, IFileStore store, CancellationToken ct) =>
        {
            var file = await db.OrderFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct);
            if (file is null) return Results.NotFound();
            var path = store.GetFullPath(file.StorageKey);
            return File.Exists(path)
                ? Results.File(path, "application/octet-stream", file.FileName, enableRangeProcessing: true)
                : Results.Problem("This file was deleted after the retention period.", statusCode: StatusCodes.Status410Gone);
        }).RequireAuthorization(Roles.Admin);
    }
}
