using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Gdpr;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Gdpr;

/// <summary>
/// Erases the uploader's name from every document when ABP's GDPR module reports that a user asked for their data to
/// be deleted (#698).
/// <para>
/// <b>Anonymize, never delete.</b> The documents are the tenant's records, not the requester's account data, so they
/// stay; what goes is <c>FileOrigin.UploadedByUserName</c>, a display-name snapshot that otherwise outlives the
/// erasure (see <see cref="IDocumentRepository.AnonymizeUploaderAsync"/>). Identity anonymizes the account itself,
/// which is why <c>CreatorId</c> is kept. The event is published by the commercial GDPR module only, so this handler
/// stays inert until the embedding host adds that module; Extract subscribes to the framework's contract and leaves
/// the publisher to the host.
/// </para>
/// <para>
/// <b>It walks the Host and every tenant, and does not read the tenant on the event.</b> Since ABP 10.7 the event
/// carries a <c>TenantId</c> (<c>IEventDataMayHaveTenantId</c>): the GDPR module's publisher sets it to the tenant
/// the deletion was requested in (<c>GdprRequestAppService.DeleteUserDataAsync</c>, <c>TenantId = CurrentTenant.Id</c>),
/// and the event bus runs this handler inside that tenant when it is set; when it is null (a Host user) the bus
/// treats the event as tenant-less and leaves the ambient tenant as it is. That names one layer, the one the request
/// came from. One account can be a member of every tenant (the Pro edition shares users across tenants), so its
/// documents can sit in any layer, and the event cannot say which. So the walk stays (#698, decision 3): each layer
/// gets its own unit of work, and so its own DbContext, and the explicit tenant switch overrides whatever the bus
/// set. Narrowing the walk to the event's tenant would leave the name in every other layer.
/// </para>
/// <para>
/// <b>A failing layer is logged and skipped, not thrown.</b> The event bus retries a failed event as a whole, so a
/// throw here would re-run every other handler of the same event - Identity's account erasure among them - and under
/// ABP's <c>Retry</c> inbox policy it stops the queue. Everything here is idempotent, but nothing retries on its own:
/// a skipped layer stays un-erased until the event is published again. Two things are deliberately not swallowed:
/// cancellation (every remaining layer would fail the same way, and the event must not look handled), and a failure
/// to list the tenants (nothing has been changed yet, and the store being down fails the other handlers too).
/// </para>
/// <para>
/// <b>Known limit:</b> documents are found by <c>CreatorId</c>, which ABP does not record when the document's tenant
/// differs from the signed-in user's (a Host user uploading while acting inside a tenant). Those names are not
/// reached; see <see cref="IDocumentRepository.AnonymizeUploaderAsync"/>.
/// </para>
/// </summary>
public class GdprUserDataDeletionEventHandler
    : IDistributedEventHandler<GdprUserDataDeletionRequestedEto>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly ITenantStore _tenantStore;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ILogger<GdprUserDataDeletionEventHandler> _logger;

    public GdprUserDataDeletionEventHandler(
        IDocumentRepository documentRepository,
        ITenantStore tenantStore,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        ILogger<GdprUserDataDeletionEventHandler> logger)
    {
        _documentRepository = documentRepository;
        _tenantStore = tenantStore;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _logger = logger;
    }

    public virtual async Task HandleEventAsync(GdprUserDataDeletionRequestedEto eventData)
    {
        var userId = eventData.UserId;
        if (userId == Guid.Empty)
        {
            _logger.LogWarning("Ignoring a GDPR user-data deletion event that carries no user id.");
            return;
        }

        foreach (var tenantId in await GetLayersAsync())
        {
            try
            {
                await AnonymizeInLayerAsync(tenantId, userId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "GDPR erasure of user {UserId}: the uploader name was NOT erased in {Layer}. Publish the deletion " +
                    "event again to retry; it is idempotent.",
                    userId, Describe(tenantId));
            }
        }
    }

    /// <summary>
    /// Every layer a user's documents can be in: the Host (<c>null</c>) and each tenant, inactive ones included - a
    /// disabled tenant's documents still carry the name. The tenant on the event is deliberately not used to shorten
    /// this list; see the class remarks.
    /// </summary>
    protected virtual async Task<IReadOnlyList<Guid?>> GetLayersAsync()
    {
        var tenants = await _tenantStore.GetListAsync();
        return new Guid?[] { null }
            .Concat(tenants.Select(tenant => (Guid?)tenant.Id))
            .ToList();
    }

    protected virtual async Task AnonymizeInLayerAsync(Guid? tenantId, Guid userId)
    {
        using (_currentTenant.Change(tenantId))
        using (var unitOfWork = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var erased = await _documentRepository.AnonymizeUploaderAsync(userId);
            await unitOfWork.CompleteAsync();

            if (erased > 0)
            {
                _logger.LogInformation(
                    "GDPR erasure of user {UserId}: erased the uploader name on {Count} document(s) in {Layer}.",
                    userId, erased, Describe(tenantId));
            }
        }
    }

    private static string Describe(Guid? tenantId) => tenantId.HasValue ? $"tenant {tenantId}" : "the Host";
}
