namespace Capstone.Curation.Application.Abstractions;

/// <summary>
/// Commits the aggregate change and the record of what happened, together.
/// </summary>
/// <remarks>
/// The implementation is expected to drain every loaded aggregate's domain
/// events, translate them into integration events, and write those to the
/// outbox table inside the same transaction as the state change - which is the
/// entire lesson of Day 20, restated as a seam. The handler below therefore
/// never publishes anything itself, and cannot: there is no publisher port in
/// the application layer at all. If there were, someone would eventually call
/// it after SaveChanges, and the gap between those two lines is exactly where
/// messages get lost.
/// </remarks>
public interface IUnitOfWork
{
    Task<int> CommitAsync(CancellationToken cancellationToken);
}
