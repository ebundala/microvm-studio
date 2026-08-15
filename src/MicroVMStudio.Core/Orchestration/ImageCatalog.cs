using System.Diagnostics;
using Google.Protobuf;
using MicroVMStudio.Core.Data;
using MicroVMStudio.Core.Helper;
using MicroVMStudio.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mvm.Helper.V1;

namespace MicroVMStudio.Core.Orchestration;

/// <summary>
/// Use case 1: define rootfs images from the vendored build pipeline.
///
/// A build is `make` shelled out to as the *unprivileged* user, because it needs
/// the docker group and nothing more. Only the final artifact crosses the
/// privilege boundary — streamed to the helper, which content-addresses it into
/// the root-owned images directory and hands back a digest to register here.
///
/// Distro discovery is a directory listing, so dropping a new
/// images/rootfs/distros/&lt;name&gt;/Dockerfile in makes it appear in the UI with
/// no code change. That is the extension point.
/// </summary>
public sealed class ImageCatalog(
    ISqliteWriter writer,
    HelperClient helper,
    IServiceScopeFactory scopes,
    IOptions<StudioOptions> options,
    ILogger<ImageCatalog> logger)
{
    private const int UploadChunkBytes = 256 * 1024;

    private readonly StudioOptions _options = options.Value;

    public async Task<IReadOnlyList<VmImage>> ListAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        return await db.Images.AsNoTracking()
            .OrderBy(i => i.Kind).ThenBy(i => i.Name)
            .ToListAsync(ct);
    }

    /// <summary>How many VMs reference each image, so the UI can block deletes.</summary>
    public async Task<IReadOnlyDictionary<string, int>> GetUsageAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();

        var kernels = await db.Vms.AsNoTracking()
            .GroupBy(v => v.KernelImageId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var rootfs = await db.Vms.AsNoTracking()
            .GroupBy(v => v.RootfsImageId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var usage = new Dictionary<string, int>();
        foreach (var row in kernels.Concat(rootfs))
        {
            usage[row.Id] = usage.GetValueOrDefault(row.Id) + row.Count;
        }
        return usage;
    }

    /// <summary>Distros available to build — one per Dockerfile under rootfs/distros/.</summary>
    public IReadOnlyList<string> ListDistros()
    {
        var distrosDir = Path.Combine(ResolveImagesSourceDir(), "rootfs", "distros");
        if (!Directory.Exists(distrosDir)) return [];

        return [.. Directory.EnumerateDirectories(distrosDir)
            .Where(d => File.Exists(Path.Combine(d, "Dockerfile")))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order()];
    }

    /// <summary>
    /// Builds a distro's rootfs and registers the result. Build output is
    /// streamed to <paramref name="onOutput"/> line by line: a rootfs build takes
    /// minutes and a silent progress bar is useless for diagnosing a failing
    /// Dockerfile.
    /// </summary>
    public async Task<VmImage> BuildRootfsAsync(
        string distro, Action<string> onOutput, CancellationToken ct = default)
    {
        var sourceDir = ResolveImagesSourceDir();
        var exitCode = await RunMakeAsync(sourceDir, ["rootfs", $"DISTRO={distro}"], onOutput, ct);

        if (exitCode != 0)
        {
            throw new ImageBuildException(
                $"`make rootfs DISTRO={distro}` exited with {exitCode}. See the build log above.");
        }

        var artifact = Path.Combine(sourceDir, "build", $"rootfs-{distro}.ext4");
        if (!File.Exists(artifact))
        {
            throw new ImageBuildException(
                $"The build reported success but produced no artifact at {artifact}.");
        }

        onOutput($">>> Registering {Path.GetFileName(artifact)} in the catalog…");
        return await RegisterAsync(artifact, ImageKind.Rootfs, distro, "ext4", distro, ct);
    }

    /// <summary>Downloads the prebuilt Firecracker CI kernel and registers it.</summary>
    public async Task<VmImage> BuildKernelAsync(
        Action<string> onOutput, CancellationToken ct = default)
    {
        var sourceDir = ResolveImagesSourceDir();
        var exitCode = await RunMakeAsync(sourceDir, ["kernel"], onOutput, ct);

        if (exitCode != 0) throw new ImageBuildException($"`make kernel` exited with {exitCode}.");

        var artifact = Path.Combine(sourceDir, "build", "vmlinux.bin");
        if (!File.Exists(artifact))
        {
            throw new ImageBuildException($"No kernel produced at {artifact}.");
        }

        return await RegisterAsync(artifact, ImageKind.Kernel, "vmlinux", "bin", null, ct);
    }

    /// <summary>Registers a file the user already has, without building anything.</summary>
    public Task<VmImage> ImportAsync(
        string path, ImageKind kind, string name, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"No such file: {path}", path);

        var extension = kind == ImageKind.Kernel ? "bin" : "ext4";
        return RegisterAsync(path, kind, name, extension, null, ct);
    }

    public async Task DeleteAsync(string imageId, CancellationToken ct = default)
    {
        var usage = await GetUsageAsync(ct);
        if (usage.TryGetValue(imageId, out var count) && count > 0)
        {
            throw new InvalidOperationException(
                $"{count} VM(s) still use this image. Delete them first.");
        }

        var path = await writer.WriteAsync(async (db, c) =>
        {
            var image = await db.Images.FirstOrDefaultAsync(i => i.Id == imageId, c);
            if (image is null) return null;

            db.Images.Remove(image);
            await db.SaveChangesAsync(c);
            return image.PathOnHost;
        }, ct);

        if (path is null) return;

        await helper.Client.DeleteImageFileAsync(new DeleteImageFileRequest
        {
            TraceId = Guid.NewGuid().ToString("N")[..12],
            PathOnHost = path,
        }, cancellationToken: ct);
    }

    // ── internals ───────────────────────────────────────────────────────────

    private async Task<VmImage> RegisterAsync(
        string artifactPath, ImageKind kind, string name, string extension,
        string? distroDir, CancellationToken ct)
    {
        var written = await UploadAsync(artifactPath, kind, name, extension, ct);
        var sizeBytes = new FileInfo(artifactPath).Length;

        return await writer.WriteAsync(async (db, c) =>
        {
            // Content-addressed, so rebuilding an unchanged Dockerfile yields the
            // same digest and simply refreshes the existing row rather than
            // accumulating duplicates.
            var existing = await db.Images.FirstOrDefaultAsync(i => i.Digest == written.Digest, c);
            if (existing is not null)
            {
                existing.Name = name;
                existing.PathOnHost = written.PathOnHost;
                existing.DistroDir = distroDir;
                existing.SizeBytes = sizeBytes;
                existing.BuiltAt = DateTime.UtcNow;
                await db.SaveChangesAsync(c);
                return existing;
            }

            var image = new VmImage
            {
                Id = Guid.NewGuid().ToString(),
                Kind = kind,
                Name = name,
                Digest = written.Digest,
                PathOnHost = written.PathOnHost,
                DistroDir = distroDir,
                SizeBytes = sizeBytes,
                BuiltAt = DateTime.UtcNow,
            };
            db.Images.Add(image);
            await db.SaveChangesAsync(c);

            logger.LogInformation("Registered {Kind} image {Name} ({Digest}) at {Path}",
                kind, name, written.Digest, written.PathOnHost);
            return image;
        }, ct);
    }

    private async Task<WriteImageFileResponse> UploadAsync(
        string sourcePath, ImageKind kind, string slug, string extension, CancellationToken ct)
    {
        using var call = helper.Client.WriteImageFile(cancellationToken: ct);

        // Client-streaming gRPC has no separate header message, so the first
        // chunk carries metadata with no payload and every chunk after is data.
        await call.RequestStream.WriteAsync(new WriteImageFileChunk
        {
            TraceId = Guid.NewGuid().ToString("N")[..12],
            ImagesDir = _options.ImagesDir,
            KindPrefix = kind == ImageKind.Kernel ? "kernel" : "rootfs",
            Slug = slug,
            Extension = extension,
        }, ct);

        await using (var source = File.OpenRead(sourcePath))
        {
            var buffer = new byte[UploadChunkBytes];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await call.RequestStream.WriteAsync(new WriteImageFileChunk
                {
                    Data = ByteString.CopyFrom(buffer, 0, read),
                }, ct);
            }
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }

    private static async Task<int> RunMakeAsync(
        string workingDir, string[] args, Action<string> onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/make",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };

        if (!proc.Start()) throw new ImageBuildException("Failed to start make.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        return proc.ExitCode;
    }

    /// <summary>
    /// Locates the vendored images/ tree. It sits beside the app when installed
    /// and two levels up from bin/ when running from a build directory, so both
    /// are probed rather than requiring configuration to run from source.
    /// </summary>
    private string ResolveImagesSourceDir()
    {
        if (Path.IsPathRooted(_options.ImagesSourceDir)) return _options.ImagesSourceDir;

        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && probe is not null; i++)
        {
            var candidate = Path.Combine(probe, _options.ImagesSourceDir);
            if (Directory.Exists(Path.Combine(candidate, "rootfs"))) return candidate;
            probe = Path.GetDirectoryName(probe.TrimEnd('/'));
        }

        throw new ImageBuildException(
            $"Could not find the '{_options.ImagesSourceDir}' build tree relative to "
            + $"{AppContext.BaseDirectory}. Set ImagesSourceDir to an absolute path.");
    }
}

public sealed class ImageBuildException(string message) : Exception(message);
