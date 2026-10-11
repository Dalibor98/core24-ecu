using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Core24.Web.Features.Storage;

public sealed record StoredFile(string Key, long SizeBytes, string Md5);

/// <summary>
/// Where uploaded files live. The rest of the app only knows "keys", so moving from the
/// local disk to Azure Blob or Cloudflare R2 later means writing one new class.
/// </summary>
public interface IFileStore
{
    /// <summary>Saves the stream under a new random key in <paramref name="folder"/>, computing size and MD5 on the way.</summary>
    Task<StoredFile> SaveAsync(Stream content, string folder, CancellationToken ct = default);

    /// <summary>Full path on disk, for streaming a download with Results.File.</summary>
    string GetFullPath(string key);

    void Delete(string key);
}

public class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Folder for uploaded files. Relative paths are relative to the app's content root.</summary>
    public string RootPath { get; set; } = "App_Data/files";
}

public class LocalFileStore : IFileStore
{
    private readonly string root;

    public LocalFileStore(IOptions<StorageOptions> options, IWebHostEnvironment env)
    {
        root = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.RootPath));
        Directory.CreateDirectory(root);
    }

    public async Task<StoredFile> SaveAsync(Stream content, string folder, CancellationToken ct = default)
    {
        // The name on disk is ours, never the uploader's: no "../../" tricks, no clashes.
        var key = $"{folder}/{Guid.NewGuid():N}.bin";
        var path = GetFullPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Hash while copying, so the file is read once instead of twice.
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long size = 0;
        try
        {
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                md5.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                size += read;
            }
        }
        catch
        {
            File.Delete(path); // don't leave half-written files behind
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new StoredFile(key, size, Convert.ToHexString(md5.GetHashAndReset()));
    }

    public string GetFullPath(string key)
    {
        var path = Path.GetFullPath(Path.Combine(root, key));
        // Defence in depth: a key must never point outside the storage folder.
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Storage key '{key}' points outside the storage folder.");
        return path;
    }

    public void Delete(string key)
    {
        var path = GetFullPath(key);
        if (File.Exists(path)) File.Delete(path);
    }
}
