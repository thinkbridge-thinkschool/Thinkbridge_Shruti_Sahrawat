using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace QuotesApi.Caching;

/// <summary>
/// Counts every command EF Core actually sends to the database.
/// </summary>
/// <remarks>
/// This is the honest half of Day 21's measurement. The cache can report its
/// own hit rate, but a cache reporting on itself is the least trustworthy
/// witness available: if the caching layer had a bug that let requests through,
/// its own counters would be the last place that showed up. Counting at the
/// EF command boundary measures the thing the exercise actually claims to
/// reduce - database work - rather than the thing the cache believes about
/// itself.
///
/// Registered as a singleton and attached in InfrastructureExtensions, so it
/// spans every scoped DbContext in the process rather than resetting per
/// request. It counts commands from every code path, not just the cached read,
/// which is deliberate: a load test that only hits one endpoint should see the
/// count go to nearly zero, and if it does not, something else is talking to
/// the database and that is worth seeing.
/// </remarks>
public sealed class DbCommandCounterInterceptor(CacheMetrics metrics) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        metrics.RecordDbCommand();
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        metrics.RecordDbCommand();
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        metrics.RecordDbCommand();
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        metrics.RecordDbCommand();
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        metrics.RecordDbCommand();
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        metrics.RecordDbCommand();
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
