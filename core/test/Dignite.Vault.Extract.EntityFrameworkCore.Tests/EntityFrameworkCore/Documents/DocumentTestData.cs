using System;
using System.Security.Cryptography;
using System.Text;
using Dignite.Vault.Extract.Documents;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Seeding atoms shared by the export test classes (#501 item 8, "related").
/// <para>
/// A static class rather than methods on <see cref="VaultExtractEntityFrameworkCoreTestBase"/>, which is where
/// #501 proposed them: <c>DocumentExportListParity_Tests</c> needs its own ABP module (it substitutes the blob
/// container so it can drive <c>DocumentAppService</c>), so it derives from
/// <c>VaultExtractTestBase&lt;TModule&gt;</c> and cannot inherit them. A base class only unifies helpers for
/// classes that happen to share the base; a static one unifies them for all callers.
/// </para>
/// </summary>
internal static class DocumentTestData
{
    /// <summary>Stable id derived from a key, so a test can name the row it seeded without threading a Guid.</summary>
    public static Guid DeterministicGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    public static FileOrigin NewFileOrigin(Guid documentId, string originalFileName = "invoice.pdf") => new(
        blobName: $"blobs/{documentId:N}.pdf",
        uploadedByUserName: "test-user",
        contentType: "application/pdf",
        // Unique per call: Document has a ContentHash-based duplicate gate (#411), and two seeded rows sharing a
        // hash would flag each other as suspected duplicates and change what the review filters select.
        contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
        fileSize: 1024,
        originalFileName: originalFileName);

    /// <summary>
    /// Simulates the post-classification state (#207: the document is associated to its type by the internal
    /// <c>DocumentTypeId</c>). The setter is private on the aggregate because only the pipeline may classify.
    /// </summary>
    public static void MarkClassified(Document document, Guid documentTypeId) =>
        SetPrivate(document, nameof(Document.DocumentTypeId), documentTypeId);

    /// <summary>Simulates the title extracted from Markdown. <c>Document.SetTitle</c> is internal to Domain.</summary>
    public static void SetTitle(Document document, string? title) =>
        SetPrivate(document, nameof(Document.Title), title);

    public static void SetLifecycleStatus(Document document, DocumentLifecycleStatus status) =>
        SetPrivate(document, nameof(Document.LifecycleStatus), status);

    /// <summary>
    /// Pins <c>CreationTime</c> so seeded rows can be made to tie. ABP's audit interceptor fills it on insert
    /// only when it is still <c>default</c>, so a pinned value survives.
    /// </summary>
    public static void SetCreationTime(Document document, DateTime creationTime) =>
        SetPrivate(document, nameof(Document.CreationTime), creationTime);

    private static void SetPrivate(Document document, string propertyName, object? value) =>
        typeof(Document).GetProperty(propertyName)!.SetValue(document, value);
}
