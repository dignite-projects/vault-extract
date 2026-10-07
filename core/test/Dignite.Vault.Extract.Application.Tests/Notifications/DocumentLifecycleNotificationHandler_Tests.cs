using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Security.Claims;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.Vault.Extract.Notifications;

public class DocumentLifecycleNotificationHandler_Tests
    : VaultExtractApplicationTestBase<DocumentNotificationsTestModule>
{
    private static readonly Guid Owner = Guid.NewGuid();

    private readonly IDocumentRepository _documents;
    private readonly PublishedNotifications _published;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public DocumentLifecycleNotificationHandler_Tests()
    {
        _documents = GetRequiredService<IDocumentRepository>();
        _published = GetRequiredService<PublishedNotifications>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
    }

    private static DocumentLifecycleStatusChangedEvent EventFor(Document document) =>
        new(document.Id, DocumentLifecycleStatus.Processing, document.LifecycleStatus);

    private void Store(Document document) =>
        _documents.FindAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(document);

    /// <summary>
    /// Delivers the event the way the ABP event bus does: the handler lives in a scope of its own, which is disposed
    /// the moment <c>HandleEventAsync</c> returns. Resolving it from the root instead would let a handler keep using
    /// services it must not, and hide it.
    /// </summary>
    private async Task HandleAsync(DocumentLifecycleStatusChangedEvent evt)
    {
        using var scope = ServiceProvider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DocumentLifecycleNotificationHandler>().HandleEventAsync(evt);
    }

    [Fact]
    public async Task Publishes_to_the_uploader_only_after_the_transition_commits()
    {
        var document = DocumentTestFactory.Create(DocumentLifecycleStatus.PendingReview, Owner);
        Store(document);

        using (var unitOfWork = _unitOfWorkManager.Begin())
        {
            await HandleAsync(EventFor(document));

            // Still inside the transaction that changes the lifecycle: nothing may be published yet.
            _published.All.ShouldBeEmpty();

            await unitOfWork.CompleteAsync();
        }

        var sent = _published.All.ShouldHaveSingleItem();
        sent.Name.ShouldBe(VaultExtractNotificationNames.DocumentNeedsReview);
        sent.Data.ShouldBeOfType<LocalizableMessageNotificationData>();
        sent.Entity!.EntityTypeName.ShouldBe(VaultExtractNotificationNames.DocumentEntityTypeName);
        sent.Entity.EntityId.ShouldBe(document.Id.ToString());
        sent.Severity.ShouldBe(NotificationSeverity.Warn);
        sent.UserIds.ShouldBe(new[] { Owner });
    }

    [Fact]
    public async Task The_publish_survives_the_disposal_of_the_handlers_own_scope()
    {
        // The regression behind #680's first real run: the commit callback fires after the event bus has disposed
        // the handler's scope, so a handler that publishes through a service it was constructed with fails with
        // ObjectDisposedException - which the never-throws contract then swallows, so the notification silently never
        // arrives. ScopeBoundNotificationPublisher refuses to work once its scope is gone.
        var document = DocumentTestFactory.Create(DocumentLifecycleStatus.Ready, Owner);
        Store(document);

        using (var unitOfWork = _unitOfWorkManager.Begin())
        {
            await HandleAsync(EventFor(document)); // handler scope is disposed on return
            await unitOfWork.CompleteAsync();
        }

        _published.All.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_transaction_that_rolls_back_publishes_nothing()
    {
        var document = DocumentTestFactory.Create(DocumentLifecycleStatus.Ready, Owner);
        Store(document);

        using (_unitOfWorkManager.Begin())
        {
            await HandleAsync(EventFor(document));
            // Disposed without CompleteAsync: the transition never happened.
        }

        _published.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_an_ambient_unit_of_work_it_publishes_immediately()
    {
        var document = DocumentTestFactory.Create(DocumentLifecycleStatus.Ready, Owner);
        Store(document);

        await HandleAsync(EventFor(document));

        _published.All.ShouldHaveSingleItem().Name.ShouldBe(VaultExtractNotificationNames.DocumentReady);
    }

    [Fact]
    public async Task A_publisher_failure_does_not_fail_the_transition()
    {
        var document = DocumentTestFactory.Create(DocumentLifecycleStatus.Failed, Owner);
        Store(document);
        _published.FailWith = new InvalidOperationException("store is down");

        using (var unitOfWork = _unitOfWorkManager.Begin())
        {
            await HandleAsync(EventFor(document));

            // The commit of the lifecycle change must go through even though the follow-up blows up.
            await Should.NotThrowAsync(() => unitOfWork.CompleteAsync());
        }

        _published.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transition_superseded_before_the_commit_is_not_announced()
    {
        // The handler sees Ready, but by the time the transaction has committed the document has moved on to
        // another notifiable state (a fast reclassification into review). That transition has its own event and
        // its own notification; this Ready event must not publish under the committed state's name. The committed
        // state is deliberately notifiable: with a non-notifiable one the planner would stay silent by itself and
        // the guard under test would never be exercised.
        var announced = DocumentTestFactory.Create(DocumentLifecycleStatus.Ready, Owner);
        var committed = DocumentTestFactory.Create(DocumentLifecycleStatus.PendingReview, Owner);
        _documents.FindAsync(announced.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(announced, committed);

        using (var unitOfWork = _unitOfWorkManager.Begin())
        {
            await HandleAsync(EventFor(announced));
            await unitOfWork.CompleteAsync();
        }

        _published.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_document_publishes_nothing()
    {
        var id = Guid.NewGuid();
        _documents.FindAsync(id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((Document?)null);

        await HandleAsync(
            new DocumentLifecycleStatusChangedEvent(id, DocumentLifecycleStatus.Processing, DocumentLifecycleStatus.Ready));

        _published.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transient_status_does_not_even_read_the_document()
    {
        var id = Guid.NewGuid();

        await HandleAsync(
            new DocumentLifecycleStatusChangedEvent(id, DocumentLifecycleStatus.Uploaded, DocumentLifecycleStatus.Processing));

        await _documents.DidNotReceiveWithAnyArgs().FindAsync(Guid.Empty, false, CancellationToken.None);
        _published.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sub_document_publishes_nothing_and_registers_no_callback()
    {
        var child = DocumentTestFactory.CreateDerived(DocumentLifecycleStatus.Ready, Owner);
        Store(child);

        using (var unitOfWork = _unitOfWorkManager.Begin())
        {
            await HandleAsync(EventFor(child));
            await unitOfWork.CompleteAsync();
        }

        _published.All.ShouldBeEmpty();
        // Only the initial read: the committed-state re-read is skipped because nothing was scheduled.
        await _documents.Received(1).FindAsync(child.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_rejection_by_the_uploader_is_silent_but_a_rejection_by_someone_else_is_not()
    {
        var document = DocumentTestFactory.CreateRejected(Owner, "Not what I meant");
        Store(document);

        var principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();

        using (principalAccessor.Change(new ClaimsPrincipal(new ClaimsIdentity(new[]
               {
                   new Claim(AbpClaimTypes.UserId, Owner.ToString())
               }, "test"))))
        {
            await HandleAsync(EventFor(document));
        }

        _published.All.ShouldBeEmpty();

        using (principalAccessor.Change(new ClaimsPrincipal(new ClaimsIdentity(new[]
               {
                   new Claim(AbpClaimTypes.UserId, Guid.NewGuid().ToString())
               }, "test"))))
        {
            await HandleAsync(EventFor(document));
        }

        _published.All.ShouldHaveSingleItem().Name.ShouldBe(VaultExtractNotificationNames.DocumentRejected);
    }

    [Fact]
    public async Task Publishes_in_the_documents_own_tenant()
    {
        var tenantId = Guid.NewGuid();
        var document = DocumentTestFactory.Create(DocumentLifecycleStatus.Ready, Owner, tenantId);
        Store(document);

        // The ambient tenant at the call site is the host's (none); the publisher must see the document's.
        using (var unitOfWork = _unitOfWorkManager.Begin())
        {
            await HandleAsync(EventFor(document));
            await unitOfWork.CompleteAsync();
        }

        _published.All.ShouldHaveSingleItem().TenantId.ShouldBe(tenantId);
    }
}
