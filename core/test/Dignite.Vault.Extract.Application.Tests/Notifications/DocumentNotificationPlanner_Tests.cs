using System;
using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// The decision rules, as a pure function of the document's facts (#680): which transition becomes which
/// notification, for whom, and when it stays silent.
/// </summary>
public class DocumentNotificationPlanner_Tests
{
    private static readonly Guid Owner = Guid.NewGuid();

    [Theory]
    [InlineData(DocumentLifecycleStatus.PendingReview, VaultExtractNotificationNames.DocumentNeedsReview, NotificationSeverity.Warn)]
    [InlineData(DocumentLifecycleStatus.Failed, VaultExtractNotificationNames.DocumentFailed, NotificationSeverity.Error)]
    [InlineData(DocumentLifecycleStatus.Ready, VaultExtractNotificationNames.DocumentReady, NotificationSeverity.Success)]
    public void Each_notifiable_status_maps_to_its_notification_for_the_uploader(
        DocumentLifecycleStatus status, string expectedName, NotificationSeverity expectedSeverity)
    {
        var document = DocumentTestFactory.Create(status, Owner);

        var plan = DocumentNotificationPlanner.Plan(DocumentNotificationSnapshot.From(document), actorId: null);

        plan.ShouldNotBeNull();
        plan.Name.ShouldBe(expectedName);
        plan.Severity.ShouldBe(expectedSeverity);
        plan.RecipientId.ShouldBe(Owner);
        plan.Entity.EntityTypeName.ShouldBe(VaultExtractNotificationNames.DocumentEntityTypeName);
        plan.Entity.EntityId.ShouldBe(document.Id.ToString());
    }

    [Fact]
    public void The_payload_is_a_thin_localizable_message_with_no_document_content()
    {
        var document = DocumentTestFactory.Create(DocumentLifecycleStatus.Ready, Owner);

        var plan = DocumentNotificationPlanner.Plan(DocumentNotificationSnapshot.From(document), actorId: null)!;

        var data = plan.Data.ShouldBeOfType<LocalizableMessageNotificationData>();
        data.ResourceName.ShouldBe(VaultExtractNotificationConsts.ResourceName);
        data.Name.ShouldBe("Notification:Ready:Message");
        data.Arguments.ShouldBeNull();
    }

    [Theory]
    [InlineData(DocumentLifecycleStatus.Uploaded)]
    [InlineData(DocumentLifecycleStatus.Processing)]
    public void Transient_statuses_are_not_notifiable(DocumentLifecycleStatus status)
    {
        DocumentNotificationPlanner.IsNotifiable(status).ShouldBeFalse();

        var document = DocumentTestFactory.Create(status, Owner);
        DocumentNotificationPlanner.Plan(DocumentNotificationSnapshot.From(document), actorId: null).ShouldBeNull();
    }

    [Theory]
    [InlineData(DocumentLifecycleStatus.PendingReview)]
    [InlineData(DocumentLifecycleStatus.Failed)]
    [InlineData(DocumentLifecycleStatus.Ready)]
    public void A_sub_document_never_notifies(DocumentLifecycleStatus status)
    {
        // A container of N children would otherwise notify N times for one upload.
        var document = DocumentTestFactory.CreateDerived(status, Owner);

        DocumentNotificationPlanner.Plan(DocumentNotificationSnapshot.From(document), actorId: null).ShouldBeNull();
    }

    [Theory]
    [InlineData(DocumentLifecycleStatus.PendingReview)]
    [InlineData(DocumentLifecycleStatus.Failed)]
    [InlineData(DocumentLifecycleStatus.Ready)]
    public void A_document_nobody_owns_notifies_nobody(DocumentLifecycleStatus status)
    {
        var document = DocumentTestFactory.Create(status, ownerId: null);

        DocumentNotificationPlanner.Plan(DocumentNotificationSnapshot.From(document), actorId: null).ShouldBeNull();
    }

    [Fact]
    public void A_container_that_reaches_Ready_notifies_once_for_the_upload()
    {
        var container = DocumentTestFactory.Create(DocumentLifecycleStatus.Ready, Owner);
        typeof(Document)
            .GetMethod("MarkAsContainer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(container, null);

        var plan = DocumentNotificationPlanner.Plan(DocumentNotificationSnapshot.From(container), actorId: null);

        plan.ShouldNotBeNull();
        plan.Name.ShouldBe(VaultExtractNotificationNames.DocumentReady);
    }

    [Fact]
    public void A_rejection_by_someone_else_notifies_with_the_reason()
    {
        var document = DocumentTestFactory.CreateRejected(Owner, "Scan is unreadable");

        var plan = DocumentNotificationPlanner.Plan(
            DocumentNotificationSnapshot.From(document), actorId: Guid.NewGuid());

        plan.ShouldNotBeNull();
        plan.Name.ShouldBe(VaultExtractNotificationNames.DocumentRejected);
        var data = plan.Data.ShouldBeOfType<LocalizableMessageNotificationData>();
        data.Name.ShouldBe("Notification:Rejected:Message");
        data.Arguments.ShouldNotBeNull();
        data.Arguments.Values.ShouldBe(new object[] { "Scan is unreadable" });
    }

    [Fact]
    public void A_rejection_by_the_uploader_themselves_is_silent()
    {
        var document = DocumentTestFactory.CreateRejected(Owner, "Wrong file");

        DocumentNotificationPlanner.Plan(DocumentNotificationSnapshot.From(document), actorId: Owner).ShouldBeNull();
    }

    [Fact]
    public void A_pipeline_failure_is_Failed_even_when_the_uploader_is_the_actor()
    {
        // The self-reject exemption is about Rejected only: a technical failure the uploader happens to trigger
        // (a re-parse that then fails) is still news to them.
        var document = DocumentTestFactory.Create(DocumentLifecycleStatus.Failed, Owner);

        var plan = DocumentNotificationPlanner.Plan(DocumentNotificationSnapshot.From(document), actorId: Owner);

        plan.ShouldNotBeNull();
        plan.Name.ShouldBe(VaultExtractNotificationNames.DocumentFailed);
    }
}
