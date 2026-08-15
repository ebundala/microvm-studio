using System.Security.Cryptography;
using Grpc.Core;
using Mvm.Helper.V1;

namespace MicroVMStudio.Helper.Services;

/// <summary>
/// Image catalog (kernel/rootfs files) lifecycle.
///
/// The images directory is root-owned: the unprivileged app can read existing
/// images (it needs their paths for LinkDiskIntoChroot) but cannot create them.
/// Mirrors DiskOperations — the helper does the write, content-addresses the
/// result, and returns path + digest for the app to register in SQLite.
/// </summary>
public sealed class ImageOperations(ILogger<ImageOperations> logger)
{
    public async Task<WriteImageFileResponse> WriteImageFileAsync(
        IAsyncStreamReader<WriteImageFileChunk> requestStream, CancellationToken ct)
    {
        if (!await requestStream.MoveNext(ct) || requestStream.Current is null)
        {
            throw new InvalidOperationException("WriteImageFile: empty request stream");
        }

        var first = requestStream.Current;
        var imagesDir = first.ImagesDir;
        Directory.CreateDirectory(imagesDir);

        // Stage to a temp file in the same directory — same filesystem, so the
        // final rename is atomic. A client disconnect mid-upload can then never
        // leave behind something that looks like a complete, content-addressed
        // image.
        var tempPath = Path.Combine(imagesDir, $".upload-{Guid.NewGuid():N}.tmp");

        string hashHex;
        long totalBytes = 0;
        try
        {
            await using (var dest = File.Create(tempPath))
            using (var sha256 = SHA256.Create())
            {
                await using var hashing = new CryptoStream(dest, sha256, CryptoStreamMode.Write);

                if (first.Data is { Length: > 0 })
                {
                    await hashing.WriteAsync(first.Data.Memory, ct);
                    totalBytes += first.Data.Length;
                }

                while (await requestStream.MoveNext(ct))
                {
                    var chunk = requestStream.Current;
                    if (chunk.Data is { Length: > 0 })
                    {
                        await hashing.WriteAsync(chunk.Data.Memory, ct);
                        totalBytes += chunk.Data.Length;
                    }
                }

                await hashing.FlushFinalBlockAsync(ct);
                hashHex = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        if (totalBytes == 0)
        {
            TryDelete(tempPath);
            throw new InvalidOperationException("WriteImageFile: uploaded file was empty");
        }

        var finalPath = Path.Combine(
            imagesDir, $"{first.KindPrefix}-{first.Slug}-{hashHex}.{first.Extension}");
        File.Move(tempPath, finalPath, overwrite: true);

        // Readable by the unprivileged app, writable only by root.
        File.SetUnixFileMode(finalPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        logger.LogInformation("Image file written at {Path} ({Bytes} bytes) trace={Trace}",
            finalPath, totalBytes, first.TraceId);

        return new WriteImageFileResponse
        {
            Digest = $"sha256:{hashHex}",
            PathOnHost = finalPath,
        };
    }

    public Task DeleteImageFileAsync(DeleteImageFileRequest req, CancellationToken ct)
    {
        if (File.Exists(req.PathOnHost))
        {
            File.Delete(req.PathOnHost);
            logger.LogInformation("Image file deleted at {Path} trace={Trace}",
                req.PathOnHost, req.TraceId);
        }
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best-effort */ }
    }
}
