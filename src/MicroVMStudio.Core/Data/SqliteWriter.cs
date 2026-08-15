using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MicroVMStudio.Core.Data;

/// <summary>
/// Serializes every database write through a single background task.
///
/// SQLite permits exactly one writer per *database*, not per row, so ShopCare's
/// per-agent Postgres advisory locks have no direct analogue and a per-VM
/// semaphore would still let concurrent writers collide. Funnelling all writes
/// through one consumer removes SQLITE_BUSY contention by construction. WAL
/// readers never block, so reads bypass this entirely.
/// </summary>
public interface ISqliteWriter
{
    Task<T> WriteAsync<T>(Func<StudioDbContext, CancellationToken, Task<T>> work, CancellationToken ct = default);
    Task WriteAsync(Func<StudioDbContext, CancellationToken, Task> work, CancellationToken ct = default);
}

public sealed class SqliteWriter(IServiceScopeFactory scopes, ILogger<SqliteWriter> logger)
    : BackgroundService, ISqliteWriter
{
    private const int MaxAttempts = 4;

    private readonly Channel<WorkItem> _queue =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });

    private sealed record WorkItem(
        Func<StudioDbContext, CancellationToken, Task<object?>> Work,
        TaskCompletionSource<object?> Completion,
        CancellationToken Ct);

    public async Task<T> WriteAsync<T>(
        Func<StudioDbContext, CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(
            new WorkItem(async (db, c) => await work(db, c), tcs, ct), ct);
        return (T)(await tcs.Task)!;
    }

    public Task WriteAsync(Func<StudioDbContext, CancellationToken, Task> work, CancellationToken ct = default)
        => WriteAsync<object?>(async (db, c) => { await work(db, c); return null; }, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (item.Ct.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.Ct);
                continue;
            }

            try
            {
                item.Completion.TrySetResult(await RunWithRetryAsync(item, stoppingToken));
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
            }
        }
    }

    private async Task<object?> RunWithRetryAsync(WorkItem item, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(item.Ct, stoppingToken);

        for (var attempt = 1; ; attempt++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            try
            {
                return await item.Work(db, linked.Token);
            }
            catch (SqliteException ex) when (IsRetryable(ex) && attempt < MaxAttempts)
            {
                // SQLITE_BUSY_SNAPSHOT invalidates the whole transaction, so the
                // only correct recovery is to discard this context and replay the
                // unit of work from the start — never to retry the statement.
                var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt - 1));
                logger.LogWarning(ex,
                    "SQLite write contention (code {Code}), attempt {Attempt}/{Max}; retrying in {Delay}ms",
                    ex.SqliteExtendedErrorCode, attempt, MaxAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, linked.Token);
            }
        }
    }

    private static bool IsRetryable(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6;    // SQLITE_BUSY, SQLITE_LOCKED

    public override async Task StopAsync(CancellationToken ct)
    {
        _queue.Writer.TryComplete();
        await base.StopAsync(ct);
    }
}
