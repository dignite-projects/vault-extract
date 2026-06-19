using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Timing;

namespace Dignite.DocumentAI.Documents.Pipelines.Lifecycle;

/// <summary>
/// Listens to <see cref="ContainerMarkerSetEvent"/> (#355) and publishes the outbound
/// <see cref="DocumentReclassifiedToContainerEto"/>. The event fires only on a real type→container transition
/// (a previously concrete-typed document re-recognized as a container), so this handler always corresponds to a
/// record downstream may have built and now needs to retract.
/// <para>
/// Mirror of <see cref="ContainerMarkerClearedEventHandler"/> (#349, the container→type direction). Unlike that
/// handler, this one does <b>no</b> aggregate work. Since #371 the former concrete document may already have spawned a
/// single figure sub-document (its embedded figure routed a <c>Kind=Figure</c> segment before this re-recognition),
/// but that figure is a legitimate constituent of the now-container and is <b>kept</b>, not retracted; the unified
/// pass then re-runs in container mode and idempotently adds the remaining <c>Kind=Text</c> constituents (#372). So
/// this handler's sole job is the outbound signal. It runs in the same transaction as the marker set (local event),
/// so the ETO is written to the transactional outbox atomically with the reclassification — never lost, never
/// published for a rolled-back transition.
/// </para>
/// </summary>
public class ContainerMarkerSetEventHandler
    : ILocalEventHandler<ContainerMarkerSetEvent>, ITransientDependency
{
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;

    public ContainerMarkerSetEventHandler(
        IDistributedEventBus distributedEventBus,
        IClock clock)
    {
        _distributedEventBus = distributedEventBus;
        _clock = clock;
    }

    public virtual async Task HandleEventAsync(ContainerMarkerSetEvent eventData)
    {
        await _distributedEventBus.PublishAsync(
            new DocumentReclassifiedToContainerEto
            {
                DocumentId = eventData.DocumentId,
                TenantId = eventData.TenantId,
                EventTime = _clock.Now
            });
    }
}
