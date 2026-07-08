using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class DocumentAppServiceDeleteTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

public class DocumentAppService_Delete_Tests
    : VaultExtractApplicationTestBase<DocumentAppServiceDeleteTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly ICabinetRepository _cabinetRepository;

    public DocumentAppService_Delete_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _distributedEventBus = GetRequiredService<IDistributedEventBus>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();

        // UploadAsync precondition fail-fast check: current layer must have at least one DocumentType.
        // Tests default to the "configured" path so cabinet / file validation / duplicate / recycle-bin
        // checks can run. fail-fast uses GetCountAsync() (#241, not GetListAsync). The dedicated
        // "not configured -> NoDocumentTypesConfigured" fact overrides count to 0.
        _documentTypeRepository.GetCountAsync(Arg.Any<CancellationToken>()).Returns(1L);
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletes_Document_Without_Removing_Blob()
    {
        var doc = CreateDocument();
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);

        await _appService.DeleteAsync(doc.Id);

        await _blobContainer.DidNotReceive().DeleteAsync(doc.FileOrigin.BlobName, Arg.Any<CancellationToken>());
        await _documentRepository.Received(1).DeleteAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_Publishes_DocumentDeletedEto()
    {
        var doc = CreateDocument();
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);

        await _appService.DeleteAsync(doc.Id);

        await _distributedEventBus.Received(1).PublishAsync(
            Arg.Is<DocumentDeletedEto>(e =>
                e.DocumentId == doc.Id &&
                e.TenantId == doc.TenantId),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task DeleteAsync_Throws_HasSubDocuments_When_Document_Still_Has_Live_SubDocuments()
    {
        // A container (or any source document) must not be sent to the recycle bin while it still has live derived
        // sub-documents, otherwise their OriginDocumentId provenance back-reference would dangle and their detail page
        // would fail to resolve the now-gone source.
        var doc = CreateDocument();
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        _documentRepository.AnyByOriginAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(true);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
        {
            await _appService.DeleteAsync(doc.Id);
        });

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.HasSubDocuments);

        // Fail closed: neither the soft-delete nor the DocumentDeletedEto fire when the guard trips.
        await _documentRepository.DidNotReceive().DeleteAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _distributedEventBus.DidNotReceive().PublishAsync(
            Arg.Any<DocumentDeletedEto>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task DeleteAsync_Succeeds_When_SubDocuments_Are_All_Already_Deleted()
    {
        // Children already in the recycle bin do not count (the ambient ISoftDelete filter excludes them, so
        // AnyByOriginAsync returns false): a source whose sub-documents are all already deleted can still be deleted.
        var doc = CreateDocument();
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        _documentRepository.AnyByOriginAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(false);

        await _appService.DeleteAsync(doc.Id);

        await _documentRepository.Received(1).DeleteAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _distributedEventBus.Received(1).PublishAsync(
            Arg.Is<DocumentDeletedEto>(e => e.DocumentId == doc.Id),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task RestoreAsync_Restores_Deleted_Document_And_Publishes_Event()
    {
        var doc = CreateDocument();
        doc.IsDeleted = true;
        doc.DeletionTime = DateTime.UtcNow;
        doc.DeleterId = Guid.NewGuid();

        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);

        await _appService.RestoreAsync(doc.Id);

        doc.IsDeleted.ShouldBeFalse();
        doc.DeletionTime.ShouldBeNull();
        doc.DeleterId.ShouldBeNull();
        await _documentRepository.Received(1).UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _distributedEventBus.Received(1).PublishAsync(
            Arg.Is<DocumentRestoredEto>(e =>
                e.DocumentId == doc.Id &&
                e.TenantId == doc.TenantId),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task UploadAsync_Throws_NoDocumentTypesConfigured_When_Current_Scope_Has_No_Types()
    {
        // Cover fail-fast: after removing Host startup seed, a fresh deployment / tenant's first upload
        // must create at least one DocumentType first. Without this check, upload succeeds, classification
        // candidates are empty, and the document gets stuck in PendingReview forever.
        _documentTypeRepository.GetCountAsync(Arg.Any<CancellationToken>()).Returns(0L);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
        {
            await _appService.UploadAsync(CreateUploadInput([1, 2, 3]));
        });

        exception.Code.ShouldBe(VaultExtractErrorCodes.DocumentType.NoneConfigured);
    }

    [Fact]
    public async Task UploadAsync_Throws_Duplicate_When_ContentHash_Belongs_To_Active_Document()
    {
        var existing = CreateDocumentWithContent([1, 2, 3]);
        _documentRepository.FindByContentHashAsync(
                existing.FileOrigin.ContentHash,
                Arg.Any<CancellationToken>())
            .Returns(existing);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
        {
            await _appService.UploadAsync(CreateUploadInput([1, 2, 3]));
        });

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.Duplicate);
    }

    [Fact]
    public async Task UploadAsync_Throws_RecycleBin_Error_When_ContentHash_Belongs_To_Deleted_Document()
    {
        var existing = CreateDocumentWithContent([1, 2, 3]);
        existing.IsDeleted = true;
        _documentRepository.FindByContentHashAsync(
                existing.FileOrigin.ContentHash,
                Arg.Any<CancellationToken>())
            .Returns(existing);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
        {
            await _appService.UploadAsync(CreateUploadInput([1, 2, 3]));
        });

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.InRecycleBin);
        exception.Data["ExistingDocumentId"].ShouldBe(existing.Id);
    }

    [Fact]
    public async Task UploadAsync_Files_Document_Into_Cabinet_When_CabinetId_Is_Valid()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal");
        _cabinetRepository.FindAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(cabinet);

        var input = CreateUploadInput([9, 8, 7]);
        input.CabinetId = cabinet.Id;

        await _appService.UploadAsync(input);

        // Manual assignment on upload: Document is persisted with the provided CabinetId after validating
        // current-layer cabinet existence and Cabinets permission.
        await _documentRepository.Received(1).InsertAsync(
            Arg.Is<Document>(d => d.CabinetId == cabinet.Id),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_Throws_InvalidCabinetId_When_Cabinet_Not_In_Current_Layer()
    {
        // No FindAsync setup means mock default returns null, representing missing cabinet or cross-layer
        // filtering by the ambient tenant filter. After CheckPolicyAsync(Cabinets.Default), AlwaysAllow in
        // test environment, this reaches fail-closed rejection.
        var input = CreateUploadInput([4, 5, 6]);
        input.CabinetId = Guid.NewGuid();

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UploadAsync(input));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Cabinet.InvalidId);
    }

    [Fact]
    public async Task UploadAsync_Throws_UnsupportedFileType_When_ContentType_Not_Allowed()
    {
        // #221: valid extension but content-type outside allowlist means fail-closed: no blob, no queue.
        var input = CreateUploadInput([1, 2, 3], fileName: "A.pdf", contentType: "application/zip");

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UploadAsync(input));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.UnsupportedFileType);
        await _documentRepository.DidNotReceive().InsertAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_Throws_UnsupportedFileType_When_Extension_Not_Allowed()
    {
        // #221: valid content-type but extension outside allowlist, which decides blob suffix and
        // DefaultTextExtractor dispatch, means fail-closed.
        var input = CreateUploadInput([1, 2, 3], fileName: "A.exe", contentType: "application/pdf");

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UploadAsync(input));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.UnsupportedFileType);
    }

    [Theory]
    [InlineData("data.csv", "text/csv")]
    [InlineData("data.csv", "application/csv")]
    [InlineData("data.csv", "application/vnd.ms-excel")]
    [InlineData("data.tsv", "text/tab-separated-values")]
    [InlineData("data.tsv", "text/tsv")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("REPORT.DOCX", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("slides.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("book.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task UploadAsync_Accepts_Supported_Digital_Document_Types(
        string fileName,
        string contentType)
    {
        await _appService.UploadAsync(CreateUploadInput([1, 2, 3], fileName, contentType));

        await _documentRepository.Received(1).InsertAsync(
            Arg.Is<Document>(d =>
                d.FileOrigin != null &&
                d.FileOrigin.OriginalFileName == fileName &&
                d.FileOrigin.ContentType == contentType),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("legacy.doc", "application/msword")]
    [InlineData("legacy.xls", "application/vnd.ms-excel")]
    [InlineData("macro.xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12")]
    [InlineData("readme.md", "text/markdown")]
    [InlineData("archive.zip", "application/zip")]
    [InlineData("unknown.xlsx", "application/octet-stream")]
    public async Task UploadAsync_Rejects_Digital_Types_Outside_The_Explicit_AllowList(
        string fileName,
        string contentType)
    {
        var exception = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UploadAsync(CreateUploadInput([1, 2, 3], fileName, contentType)));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.UnsupportedFileType);
    }

    [Theory]
    [InlineData("book.xlsx", "text/plain")]
    [InlineData("notes.txt", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("report.docx", "application/vnd.ms-excel")]
    [InlineData("slides.pptx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("data.csv", "text/tab-separated-values")]
    public async Task UploadAsync_Rejects_Mismatched_Allowed_Extension_And_ContentType(
        string fileName,
        string contentType)
    {
        var exception = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UploadAsync(CreateUploadInput([1, 2, 3], fileName, contentType)));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.UnsupportedFileType);
    }

    [Fact]
    public async Task UploadAsync_Throws_FileTooLarge_When_Declared_ContentLength_Exceeds_Limit()
    {
        // #221: client-declared ContentLength over limit is rejected cheaply and quickly without reading
        // the stream.
        var input = new UploadDocumentInput
        {
            File = new RemoteStreamContent(
                new MemoryStream([1, 2, 3]),
                "A.pdf",
                "application/pdf",
                readOnlyLength: DocumentConsts.MaxUploadFileBytes + 1,
                disposeStream: true)
        };

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UploadAsync(input));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.FileTooLarge);
    }

    [Fact]
    public async Task UploadAsync_Throws_FileTooLarge_When_Streamed_Bytes_Exceed_Limit_Despite_Underreported_Length()
    {
        // #221: declared ContentLength underreports and is untrusted, but streamed copy still enforces the
        // hard limit by actual byte count. Do not rely on client declarations or fully buffer oversized
        // bodies. Temporarily lower the static limit and restore it in finally; this class runs serially.
        var original = DocumentConsts.MaxUploadFileBytes;
        try
        {
            DocumentConsts.MaxUploadFileBytes = 4;
            var input = new UploadDocumentInput
            {
                File = new RemoteStreamContent(
                    new MemoryStream(new byte[10]),
                    "A.pdf",
                    "application/pdf",
                    readOnlyLength: 3, // underreported to bypass the cheap ContentLength check
                    disposeStream: true)
            };

            var exception = await Should.ThrowAsync<BusinessException>(async () =>
                await _appService.UploadAsync(input));

            exception.Code.ShouldBe(VaultExtractErrorCodes.Document.FileTooLarge);
        }
        finally
        {
            DocumentConsts.MaxUploadFileBytes = original;
        }
    }

    [Fact]
    public async Task GetFigureAsync_Serves_The_Retained_Figure_Blob_By_Manifest_Hash()
    {
        // #477: the endpoint resolves the request hash against the retained-figure manifest and serves the blob by
        // the manifest's OWN stored key + content type (never a key built from the request).
        var doc = CreateDocument();
        const string hash = "abc123";
        var blobName = $"extraction-figures/{doc.Id}/{hash}";
        SetExtractionMetadata(doc, new DocumentParseMetadata(
            "PdfPig", null, figures: new[] { new FigureManifestEntry(blobName, hash, "image/png", 2048) }));

        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _blobContainer.GetAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 1, 2, 3 }));

        var result = await _appService.GetFigureAsync(doc.Id, $"{hash}.png");

        result.ContentType.ShouldBe("image/png");
        await _blobContainer.Received(1).GetAsync(blobName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFigureAsync_Throws_FigureNotFound_For_An_Unknown_Hash_Without_Touching_Blob_Storage()
    {
        // Security: a hash not in the manifest never reaches blob storage (no arbitrary / traversal fetch).
        var doc = CreateDocument();
        SetExtractionMetadata(doc, new DocumentParseMetadata(
            "PdfPig", null,
            figures: new[] { new FigureManifestEntry($"extraction-figures/{doc.Id}/known", "known", "image/png", 10) }));

        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);

        var ex = await Should.ThrowAsync<BusinessException>(
            () => _appService.GetFigureAsync(doc.Id, "unknown.png"));
        ex.Code.ShouldBe(VaultExtractErrorCodes.Document.FigureNotFound);
        await _blobContainer.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PermanentDeleteAsync_Skips_A_Manifest_Figure_Blob_Still_Referenced_By_A_SubDocument()
    {
        // #478 owner side: the source's manifest reclaim must NOT delete a figure blob a sub-document's FileOrigin
        // still shares — the sub-document outlives its source; its own permanent delete reclaims the blob later.
        var doc = CreateDocument();
        var sharedBlob = $"extraction-figures/{doc.Id}/imghash";
        SetExtractionMetadata(doc, new DocumentParseMetadata(
            "PdfPig", null, figures: new[] { new FigureManifestEntry(sharedBlob, "imghash", "image/png", 10) }));

        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository
            .AnyWithFileOriginBlobNameAsync(sharedBlob, doc.Id, Arg.Any<CancellationToken>())
            .Returns(true); // a live (or restorable) sub-document still references it

        await _appService.PermanentDeleteAsync(doc.Id);

        await _blobContainer.DidNotReceive().DeleteAsync(sharedBlob, Arg.Any<CancellationToken>());
        // The document's own (unshared, GUID-keyed) upload blob is still reclaimed as always.
        await _blobContainer.Received(1).DeleteAsync(doc.FileOrigin!.BlobName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PermanentDeleteAsync_Reclaims_A_Manifest_Figure_Blob_Nothing_References()
    {
        var doc = CreateDocument();
        var figureBlob = $"extraction-figures/{doc.Id}/imghash";
        SetExtractionMetadata(doc, new DocumentParseMetadata(
            "PdfPig", null, figures: new[] { new FigureManifestEntry(figureBlob, "imghash", "image/png", 10) }));

        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        // AnyWithFileOriginBlobNameAsync substitute default = false: no reference remains.

        await _appService.PermanentDeleteAsync(doc.Id);

        await _blobContainer.Received(1).DeleteAsync(figureBlob, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PermanentDeleteAsync_Keeps_A_Borrowed_Figure_Blob_While_Its_Owner_Exists()
    {
        // #478 borrower side: a figure sub-document's FileOrigin SHARES its source's extraction-figures blob.
        // While the owner row exists (even soft-deleted — restorable), the owner's manifest reclaim governs the
        // blob, so the sub-document's own permanent delete must not touch it.
        var ownerId = Guid.NewGuid();
        var borrowedBlob = $"extraction-figures/{ownerId}/imghash";
        var subDoc = CreateDocumentWithFileOrigin(borrowedBlob);

        _documentRepository.GetAsync(subDoc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(subDoc);
        _documentRepository.FindAsync(ownerId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(CreateDocument()); // the owner still exists

        await _appService.PermanentDeleteAsync(subDoc.Id);

        await _blobContainer.DidNotReceive().DeleteAsync(borrowedBlob, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PermanentDeleteAsync_Reclaims_A_Borrowed_Figure_Blob_Once_The_Owner_Is_Gone()
    {
        // The last referencing side reclaims: owner hard-deleted (its manifest reclaim skipped the blob because
        // this sub-document still referenced it), no sibling references it → this delete reclaims the blob.
        var ownerId = Guid.NewGuid();
        var borrowedBlob = $"extraction-figures/{ownerId}/imghash";
        var subDoc = CreateDocumentWithFileOrigin(borrowedBlob);

        _documentRepository.GetAsync(subDoc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(subDoc);
        // FindAsync default = null (owner gone); AnyWithFileOriginBlobNameAsync default = false (no sibling).

        await _appService.PermanentDeleteAsync(subDoc.Id);

        await _blobContainer.Received(1).DeleteAsync(borrowedBlob, Arg.Any<CancellationToken>());
    }

    private static Document CreateDocumentWithFileOrigin(string blobName)
    {
        return new Document(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new FileOrigin(
                blobName: blobName,
                uploadedByUserName: "test-user",
                contentType: "image/png",
                contentHash: "imghash",
                fileSize: 10,
                originalFileName: "figure-p1.png"));
    }

    // Document.SetExtractionMetadata is internal and InternalsVisibleTo covers only EntityFrameworkCore.Tests, so
    // set it via reflection here rather than broaden internal visibility for a test convenience.
    private static void SetExtractionMetadata(Document document, DocumentParseMetadata metadata)
        => typeof(Document)
            .GetMethod("SetExtractionMetadata",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(document, new object?[] { metadata });

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }

    private static Document CreateDocumentWithContent(byte[] bytes)
    {
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

        return new Document(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: contentHash,
                fileSize: bytes.LongLength,
                originalFileName: "test.pdf"));
    }

    private static UploadDocumentInput CreateUploadInput(
        byte[] bytes, string fileName = "A.pdf", string contentType = "application/pdf")
    {
        return new UploadDocumentInput
        {
            File = new RemoteStreamContent(
                new MemoryStream(bytes),
                fileName,
                contentType,
                disposeStream: true)
        };
    }
}
