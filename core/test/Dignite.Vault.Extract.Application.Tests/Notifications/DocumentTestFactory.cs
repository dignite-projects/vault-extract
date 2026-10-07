using System;
using System.Reflection;
using Dignite.Vault.Extract.Documents;

namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// Builds <see cref="Document"/>s in the lifecycle state a test needs. The state writers are internal to the
/// domain (the pipeline owns them), so tests reach them the way the Application tests do.
/// </summary>
internal static class DocumentTestFactory
{
    public static Document Create(
        DocumentLifecycleStatus status,
        Guid? ownerId,
        Guid? tenantId = null)
    {
        var document = new Document(
            Guid.NewGuid(),
            tenantId,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        SetCreator(document, ownerId);
        Transition(document, status);
        return document;
    }

    public static Document CreateDerived(DocumentLifecycleStatus status, Guid ownerId)
    {
        var document = Document.CreateDerived(
            Guid.NewGuid(), tenantId: null, fileOrigin: null,
            originDocumentId: Guid.NewGuid(), originConstituentKey: "segment-1", creatorId: ownerId);
        Transition(document, status);
        return document;
    }

    public static Document CreateRejected(Guid? ownerId, string reason)
    {
        var document = Create(DocumentLifecycleStatus.Processing, ownerId);
        document.RejectReview(reason);
        return document;
    }

    public static void Transition(Document document, DocumentLifecycleStatus status) =>
        typeof(Document)
            .GetMethod("TransitionLifecycle", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(document, new object[] { status });

    private static void SetCreator(Document document, Guid? ownerId) =>
        typeof(Document)
            .GetProperty("CreatorId", BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(document, ownerId);
}
