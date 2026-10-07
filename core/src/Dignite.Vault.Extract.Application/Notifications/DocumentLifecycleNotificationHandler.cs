using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;
using Volo.Abp.Uow;
using Volo.Abp.Users;

namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// Turns <see cref="DocumentLifecycleStatusChangedEvent"/> into an in-app notification for the uploader (#680).
/// <para>
/// <b>Publishes after the transition commits, in a unit of work of its own, and only logs on failure.</b> The local
/// event is handled inside the transaction that changes the document's lifecycle, so publishing there would let a
/// notification error roll the transition back (or, for an exception swallowed mid-transaction, poison it). A
/// notification is a courtesy, never a reason to fail a document or to land in the background-job retry loop.
/// The price is that a crash between commit and publish loses one notification; operator notifications are
/// best-effort by design, unlike the outbox-backed <c>Document*Eto</c> egress.
/// </para>
/// <para>
/// <b>The deferred work runs in a scope of its own.</b> The event bus gives each handler call its own DI scope and
/// disposes it the moment <see cref="HandleEventAsync"/> returns, which is before the commit callback fires. Anything
/// this class was constructed with (other than the scope factory, which outlives every scope) is dead by then, so the
/// callback builds a fresh scope and resolves the <see cref="DocumentNotificationDispatcher"/> from it.
/// </para>
/// <para>
/// The dispatcher reads the document again after the commit and publishes only if it is still in the state the event
/// announced, the same defensive check <c>DocumentReadyEventHandler</c> makes before releasing a document, so a
/// transition superseded within the same transaction does not notify.
/// </para>
/// </summary>
public class DocumentLifecycleNotificationHandler
    : ILocalEventHandler<DocumentLifecycleStatusChangedEvent>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<DocumentLifecycleNotificationHandler> _logger;

    public DocumentLifecycleNotificationHandler(
        IDocumentRepository documentRepository,
        IUnitOfWorkManager unitOfWorkManager,
        IServiceScopeFactory serviceScopeFactory,
        ICurrentUser currentUser,
        ILogger<DocumentLifecycleNotificationHandler> logger)
    {
        _documentRepository = documentRepository;
        _unitOfWorkManager = unitOfWorkManager;
        _serviceScopeFactory = serviceScopeFactory;
        _currentUser = currentUser;
        _logger = logger;
    }

    public virtual async Task HandleEventAsync(DocumentLifecycleStatusChangedEvent eventData)
    {
        if (!DocumentNotificationPlanner.IsNotifiable(eventData.NewStatus))
        {
            return;
        }

        var document = await _documentRepository.FindAsync(eventData.DocumentId, includeDetails: false);
        if (document == null)
        {
            return;
        }

        var snapshot = DocumentNotificationSnapshot.From(document);
        var actorId = _currentUser.Id;

        // Nothing to say for this document (derived, ownerless, self-rejected): do not register a callback at all.
        if (DocumentNotificationPlanner.Plan(snapshot, actorId) == null)
        {
            return;
        }

        var currentUnitOfWork = _unitOfWorkManager.Current;
        if (currentUnitOfWork == null)
        {
            await DispatchAsync(snapshot, actorId);
            return;
        }

        currentUnitOfWork.OnCompleted(() => DispatchAsync(snapshot, actorId));
    }

    protected virtual async Task DispatchAsync(DocumentNotificationSnapshot snapshot, Guid? actorId)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<DocumentNotificationDispatcher>()
                .DispatchAsync(snapshot, actorId);
        }
        catch (Exception ex)
        {
            // The dispatcher already swallows its own failures; this covers building the scope itself.
            _logger.LogError(
                ex,
                "Failed to dispatch the {Status} notification for document {DocumentId}; the lifecycle transition is unaffected.",
                snapshot.Status, snapshot.DocumentId);
        }
    }
}
