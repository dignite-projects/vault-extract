using System;
using System.Threading.Tasks;
using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// Publishes the notification for a lifecycle transition that has already committed, in a unit of work of its own.
/// <para>
/// Resolved from a scope created for the purpose by <see cref="DocumentLifecycleNotificationHandler"/>, never from
/// the handler's own: the event bus builds one scope per handler call and disposes it as soon as
/// <c>HandleEventAsync</c> returns, and this runs after that.
/// </para>
/// <para>
/// <b>Never throws.</b> A notification is a courtesy: whatever goes wrong is logged and dropped, so it can neither
/// undo the transition that already committed nor land in a background-job retry loop.
/// </para>
/// </summary>
public class DocumentNotificationDispatcher : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly INotificationPublisher _notificationPublisher;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<DocumentNotificationDispatcher> _logger;

    public DocumentNotificationDispatcher(
        IDocumentRepository documentRepository,
        INotificationPublisher notificationPublisher,
        IUnitOfWorkManager unitOfWorkManager,
        ICurrentTenant currentTenant,
        ILogger<DocumentNotificationDispatcher> logger)
    {
        _documentRepository = documentRepository;
        _notificationPublisher = notificationPublisher;
        _unitOfWorkManager = unitOfWorkManager;
        _currentTenant = currentTenant;
        _logger = logger;
    }

    /// <param name="expected">The document as the transition event announced it.</param>
    /// <param name="actorId">Who caused the transition, or <c>null</c> for the pipeline.</param>
    public virtual async Task DispatchAsync(DocumentNotificationSnapshot expected, Guid? actorId)
    {
        try
        {
            // INotificationPublisher records CurrentTenant.Id, and nothing guarantees the ambient tenant here is the
            // document's, so name it explicitly (null = host). Transactional on purpose: the inbox rows (the
            // notification store's DbContext) and the delivery event (the host's outbox) share the default
            // connection, so one transaction is what makes "persisted" and "will be delivered" agree.
            using (_currentTenant.Change(expected.TenantId))
            using (var unitOfWork = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true))
            {
                var current = await _documentRepository.FindAsync(expected.DocumentId, includeDetails: false);
                if (current == null || current.LifecycleStatus != expected.Status)
                {
                    _logger.LogInformation(
                        "Document {DocumentId} is no longer {Status} after commit; notification suppressed.",
                        expected.DocumentId, expected.Status);
                    return;
                }

                var notification = DocumentNotificationPlanner.Plan(DocumentNotificationSnapshot.From(current), actorId);
                if (notification == null)
                {
                    return;
                }

                await _notificationPublisher.PublishAsync(
                    notification.Name,
                    notification.Data,
                    notification.Entity,
                    notification.Severity,
                    userIds: new[] { notification.RecipientId });

                await unitOfWork.CompleteAsync();
            }
        }
        catch (Exception ex)
        {
            // The recipient id is deliberately not logged.
            _logger.LogError(
                ex,
                "Failed to publish the {Status} notification for document {DocumentId}; the lifecycle transition is unaffected.",
                expected.Status, expected.DocumentId);
        }
    }
}
