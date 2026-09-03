namespace Capstone.SharedKernel;

/// <summary>
/// Something that happened in the domain, stated in the past tense, that the
/// rest of the system may care about.
/// </summary>
/// <remarks>
/// Deliberately carries no infrastructure of any kind - no message id, no
/// topic, no serialisation attributes. A domain event is an in-process fact
/// owned by the module that raised it. Turning one into something another
/// module can subscribe to is a translation step that happens at the module
/// boundary (see the Contracts projects), and keeping that translation
/// explicit is what stops one module's internal model leaking into another
/// module's subscription contract.
/// </remarks>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
