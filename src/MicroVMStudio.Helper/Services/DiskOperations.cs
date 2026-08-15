using Mvm.Helper.V1;

namespace MicroVMStudio.Helper.Services;

/// <summary>
/// Disk file lifecycle: create a sized, formatted, per-VM-owned image; link it
/// into the jailer chroot so Firecracker can open it post-chroot; delete it.
/// </summary>
public sealed class DiskOperations(Shell shell, ILogger<DiskOperations> logger)
{
    public async Task CreateDiskImageAsync(CreateDiskImageRequest req, CancellationToken ct)
    {
        var path = req.PathOnHost;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            // Idempotent, but a size mismatch means the caller and the host
            // disagree about what this disk is — fail loudly rather than boot a
            // VM against a wrongly-sized disk.
            var actual = new FileInfo(path).Length;
            var expected = (long)req.SizeGib * 1024L * 1024L * 1024L;
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"Disk file at {path} already exists with size {actual} bytes; expected {expected}.");
            }
            return;
        }

        // Sparse allocation: instant and near-zero IO. Firecracker is perfectly
        // happy with sparse backing files.
        await shell.RunAsync("/usr/bin/truncate", ["-s", $"{req.SizeGib}G", path],
            throwOnNonZero: true, ct: ct);

        // Format on the host, at creation time only. The guest rootfs is minimal
        // and has no e2fsprogs, and the already-exists branch above returns early
        // so a rebooting VM can never be reformatted out from under the overlay
        // that uses this disk as its upper layer.
        await shell.RunAsync("/usr/sbin/mkfs.ext4", ["-q", "-F", path],
            throwOnNonZero: true, ct: ct);

        await shell.RunAsync("/bin/chown", [$"{req.OwnerUid}:{req.OwnerGid}", path],
            throwOnNonZero: true, ct: ct);
        await shell.RunAsync("/bin/chmod", ["0600", path], throwOnNonZero: true, ct: ct);

        logger.LogInformation(
            "Disk {DiskId} created at {Path} size={Size}GiB owner={Uid}:{Gid} trace={Trace}",
            req.DiskId, path, req.SizeGib, req.OwnerUid, req.OwnerGid, req.TraceId);
    }

    public async Task LinkDiskIntoChrootAsync(LinkDiskIntoChrootRequest req, CancellationToken ct)
    {
        var rootDir = req.ChrootRootPath;
        Directory.CreateDirectory(rootDir);

        var targetName = string.IsNullOrEmpty(req.TargetName) ? "rootfs.img" : req.TargetName;
        var linkPath = Path.Combine(rootDir, targetName);

        if (File.Exists(linkPath))
        {
            // Already linked. If it points at a different inode it is stale from
            // a prior boot with a different image — unlink and relink.
            if (await SameInodeAsync(linkPath, req.DiskHostPath, ct)) return;
            File.Delete(linkPath);
        }

        if (req.UseBindMount)
        {
            await shell.RunAsync("/bin/mount", ["--bind", req.DiskHostPath, linkPath],
                throwOnNonZero: true, ct: ct);
        }
        else
        {
            // Hard link: same inode, so kernel and rootfs images are shared
            // across every VM using them. Requires source and chroot to be on
            // the same filesystem — both live under BaseDir, so they are.
            await shell.RunAsync("/bin/ln", [req.DiskHostPath, linkPath],
                throwOnNonZero: true, ct: ct);
        }

        logger.LogInformation("Disk {DiskId} linked into chroot at {LinkPath} mode={Mode} trace={Trace}",
            req.DiskId, linkPath, req.UseBindMount ? "bind" : "hardlink", req.TraceId);
    }

    public async Task DeleteDiskImageAsync(DeleteDiskImageRequest req, CancellationToken ct)
    {
        if (!File.Exists(req.PathOnHost)) return;

        if (req.SecureOverwrite)
        {
            var shred = await shell.RunAsync("/usr/bin/shred", ["-uz", "-n", "1", req.PathOnHost], ct: ct);
            if (shred.Succeeded)
            {
                logger.LogInformation("Disk {DiskId} shredded trace={Trace}", req.DiskId, req.TraceId);
                return;
            }
            // shred missing or failed — fall through to a single zero pass.
            await shell.RunAsync("/bin/dd",
                ["if=/dev/zero", $"of={req.PathOnHost}", "bs=1M", "conv=notrunc,fdatasync"], ct: ct);
        }

        File.Delete(req.PathOnHost);
        logger.LogInformation("Disk {DiskId} deleted at {Path} secure={Secure} trace={Trace}",
            req.DiskId, req.PathOnHost, req.SecureOverwrite, req.TraceId);
    }

    /// <summary>
    /// Every *.img in the disks directory with its real on-disk usage. The app
    /// diffs this against live VmDisk rows to find files whose database row was
    /// hard-deleted while the unlink failed.
    /// </summary>
    public async Task<ListDiskFilesResponse> ListDiskFilesAsync(ListDiskFilesRequest req, CancellationToken ct)
    {
        var response = new ListDiskFilesResponse();
        if (!Directory.Exists(req.DisksDir)) return response;

        // `--block-size=1 --all` reports actual on-disk usage. Not `-b`, which is
        // shorthand for --apparent-size and would report the logical size — for a
        // sparse image that is exactly the wrong number. `-s` is omitted because
        // it conflicts with --all.
        var r = await shell.RunAsync("/usr/bin/du", ["--block-size=1", "--all", req.DisksDir], ct: ct);
        if (!r.Succeeded) return response;

        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var path = line[(tab + 1)..].Trim();
            if (!path.EndsWith(".img", StringComparison.Ordinal)) continue;
            if (!long.TryParse(line[..tab], out var bytes)) continue;

            response.Files.Add(new HostDiskFile { PathOnHost = path, SizeBytes = bytes });
        }
        return response;
    }

    private async Task<bool> SameInodeAsync(string a, string b, CancellationToken ct)
    {
        var ra = await shell.RunAsync("/usr/bin/stat", ["-c", "%i", a], ct: ct);
        var rb = await shell.RunAsync("/usr/bin/stat", ["-c", "%i", b], ct: ct);
        if (!ra.Succeeded || !rb.Succeeded) return false;
        return ra.Stdout.Trim() == rb.Stdout.Trim();
    }
}
