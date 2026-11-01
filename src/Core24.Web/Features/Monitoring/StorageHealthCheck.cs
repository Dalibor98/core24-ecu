using Core24.Web.Features.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Core24.Web.Features.Monitoring;

/// <summary>
/// "Can we still save uploads?" A full disk or a lost volume mount would otherwise only show up
/// when the first customer's upload fails.
/// </summary>
public class StorageHealthCheck(IFileStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var probe = new MemoryStream([1, 2, 3]);
            var saved = await store.SaveAsync(probe, "health", ct);
            store.Delete(saved.Key);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("File storage is not writable.", ex);
        }
    }
}
