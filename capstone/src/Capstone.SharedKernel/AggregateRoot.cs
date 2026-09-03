namespace Capstone.SharedKernel;

/// <summary>
/// Base class for an aggregate root: the single entry point to a consistency
/// boundary, and the only thing a repository is allowed to load or save.
/// </summary>
/// <remarks>
/// The events list is the important part. An aggregate that mutates state and
/// then relies on the caller to remember to publish something has two sources
/// of truth about what happened, and they drift the first time somebody adds a
/// second call site. Here the aggregate records the fact itself, and the
/// infrastructure drains the list inside the same transaction that saves the
/// state change - which is exactly the mechanism Day 20's outbox already
/// proved: the state change and the record of it commit together or not at all.
/// </remarks>
public abstract class AggregateRoot<TId>
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public TId Id { get; protected set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>
    /// Called by infrastructure once the events have been drained into the
    /// outbox, so a second SaveChanges cannot publish them twice.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
