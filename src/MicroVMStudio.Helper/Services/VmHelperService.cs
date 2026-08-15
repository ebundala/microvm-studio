using Grpc.Core;
using Mvm.Helper.V1;

namespace MicroVMStudio.Helper.Services;

/// <summary>
/// gRPC facade. Holds no logic of its own — it validates, delegates to the
/// operation classes, and maps exceptions onto typed gRPC statuses so the app
/// can turn them into lifecycle states.
/// </summary>
public sealed class VmHelperService(
    NetnsOperations netns,
    IptablesOperations iptables,
    DiskOperations disks,
    JailerOperations jailer,
    FirecrackerApiProxy fcApi,
    ImageOperations images,
    ILogger<VmHelperService> logger) : VmHelper.VmHelperBase
{
    public override async Task<PingResponse> Ping(Empty request, ServerCallContext context)
    {
        var egress = await netns.DetectEgressInterfaceAsync(context.CancellationToken);
        return new PingResponse
        {
            Version = typeof(VmHelperService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            FirecrackerPresent = Shell.BinaryExists("/usr/local/bin/firecracker"),
            JailerPresent = Shell.BinaryExists("/usr/local/bin/jailer"),
            KvmAccessible = File.Exists("/dev/kvm"),
            CgroupV2 = Directory.Exists("/sys/fs/cgroup/cgroup.controllers")
                       || File.Exists("/sys/fs/cgroup/cgroup.controllers"),
            EgressInterface = egress,
        };
    }

    // ── networking ──────────────────────────────────────────────────────────

    public override Task<EnsureHostBridgeResponse> EnsureHostBridge(
        EnsureHostBridgeRequest request, ServerCallContext context) =>
        Guard(() => netns.EnsureHostBridgeAsync(request, context.CancellationToken), request.TraceId);

    public override async Task<Empty> DestroyHostBridge(
        DestroyHostBridgeRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await netns.DestroyHostBridgeAsync(request, context.CancellationToken);
            return new Empty();
        }, request.TraceId);

    public override Task<CreateVmNetnsResponse> CreateVmNetns(
        CreateVmNetnsRequest request, ServerCallContext context) =>
        Guard(() => netns.CreateVmNetnsAsync(request, context.CancellationToken), request.TraceId);

    public override async Task<Empty> DestroyVmNetns(
        DestroyVmNetnsRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await netns.DestroyVmNetnsAsync(request, context.CancellationToken);
            return new Empty();
        }, request.TraceId);

    public override async Task<Empty> ApplyVmEgressRules(
        ApplyVmEgressRulesRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await iptables.ApplyVmEgressRulesAsync(request, context.CancellationToken);
            return new Empty();
        }, request.TraceId);

    // ── disks ───────────────────────────────────────────────────────────────

    public override async Task<Empty> CreateDiskImage(
        CreateDiskImageRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await disks.CreateDiskImageAsync(request, context.CancellationToken);
            return new Empty();
        }, request.TraceId);

    public override async Task<Empty> LinkDiskIntoChroot(
        LinkDiskIntoChrootRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await disks.LinkDiskIntoChrootAsync(request, context.CancellationToken);
            return new Empty();
        }, request.TraceId);

    public override async Task<Empty> DeleteDiskImage(
        DeleteDiskImageRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await disks.DeleteDiskImageAsync(request, context.CancellationToken);
            return new Empty();
        }, request.TraceId);

    public override Task<ListDiskFilesResponse> ListDiskFiles(
        ListDiskFilesRequest request, ServerCallContext context) =>
        Guard(() => disks.ListDiskFilesAsync(request, context.CancellationToken), "");

    // ── jailer / firecracker ────────────────────────────────────────────────

    public override Task<LaunchJailerResponse> LaunchJailer(
        LaunchJailerRequest request, ServerCallContext context) =>
        Guard(() => jailer.LaunchJailerAsync(request, context.CancellationToken), request.TraceId);

    public override async Task<Empty> KillJailer(
        KillJailerRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await jailer.KillJailerAsync(request, context.CancellationToken);
            return new Empty();
        }, request.TraceId);

    public override Task<ReadJailerStatusResponse> ReadJailerStatus(
        ReadJailerStatusRequest request, ServerCallContext context) =>
        Guard(() => jailer.ReadJailerStatusAsync(request, context.CancellationToken), request.TraceId);

    public override async Task<Empty> DeleteJailerChroot(
        DeleteJailerChrootRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await jailer.DeleteJailerChrootAsync(request, context.CancellationToken);
            // The VM is gone; drop its pooled FC API connection so a future VM
            // reusing the id can't inherit a dead socket.
            fcApi.Forget(request.VmId);
            return new Empty();
        }, request.TraceId);

    public override Task<FirecrackerApiCallResponse> FirecrackerApiCall(
        FirecrackerApiCallRequest request, ServerCallContext context) =>
        // Deliberately not wrapped in Guard: this RPC reports transport failures
        // in-band via error_code so the caller can distinguish "FC said no" from
        // "FC unreachable" without catching exceptions.
        fcApi.CallAsync(request, context.CancellationToken);

    // ── images ──────────────────────────────────────────────────────────────

    public override Task<WriteImageFileResponse> WriteImageFile(
        IAsyncStreamReader<WriteImageFileChunk> requestStream, ServerCallContext context) =>
        Guard(() => images.WriteImageFileAsync(requestStream, context.CancellationToken), "");

    public override async Task<Empty> DeleteImageFile(
        DeleteImageFileRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await images.DeleteImageFileAsync(request, context.CancellationToken);
            return new Empty();
        }, request.TraceId);

    // ── diagnostics ─────────────────────────────────────────────────────────

    public override Task<ListHostVethsResponse> ListHostVeths(
        Empty request, ServerCallContext context) =>
        Guard(() => netns.ListHostVethsAsync(context.CancellationToken), "");

    public override async Task<Empty> DeleteHostVeth(
        DeleteHostVethRequest request, ServerCallContext context) =>
        await Guard(async () =>
        {
            await netns.DeleteHostVethAsync(request, context.CancellationToken);
            return new Empty();
        }, request.TraceId);

    public override Task<ListJailerChrootsResponse> ListJailerChroots(
        ListJailerChrootsRequest request, ServerCallContext context) =>
        Guard(() => jailer.ListJailerChrootsAsync(request, context.CancellationToken), "");

    /// <summary>
    /// Maps operation exceptions onto gRPC statuses. Without this every failure
    /// reaches the client as an opaque "Exception was thrown by handler",
    /// stripping the stderr that says why the underlying command failed.
    /// </summary>
    private async Task<T> Guard<T>(Func<Task<T>> op, string traceId)
    {
        try
        {
            return await op();
        }
        catch (ShellNonZeroException ex)
        {
            logger.LogError(ex, "Privileged command failed trace={Trace}", traceId);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        catch (TimeoutException ex)
        {
            logger.LogError(ex, "Privileged command timed out trace={Trace}", traceId);
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid helper operation trace={Trace}", traceId);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogError(ex, "Permission denied in helper trace={Trace}", traceId);
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            logger.LogError(ex, "Unhandled helper error trace={Trace}", traceId);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}
