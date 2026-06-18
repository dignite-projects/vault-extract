using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Documents;
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

namespace Dignite.DocumentAI.Documents;

[DependsOn(typeof(DocumentAIApplicationTestModule))]
public class DocumentAppServiceDeleteTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<DocumentAIDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

public class DocumentAppService_Delete_Tests
    : DocumentAIApplicationTestBase<DocumentAppServiceDeleteTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly ICabinetRepository _cabinetRepository;

    public DocumentAppService_Delete_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _distributedEventBus = GetRequiredService<IDistributedEventBus>();
        _blobContainer = GetRequiredService<IBlobContainer<DocumentAIDocumentContainer>>();
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

        exception.Code.ShouldBe(DocumentAIErrorCodes.DocumentType.NoneConfigured);
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

        exception.Code.ShouldBe(DocumentAIErrorCodes.Document.Duplicate);
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

        exception.Code.ShouldBe(DocumentAIErrorCodes.Document.InRecycleBin);
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

        exception.Code.ShouldBe(DocumentAIErrorCodes.Cabinet.InvalidId);
    }

    [Fact]
    public async Task UploadAsync_Throws_UnsupportedFileType_When_ContentType_Not_Allowed()
    {
        // #221: valid extension but content-type outside allowlist means fail-closed: no blob, no queue.
        var input = CreateUploadInput([1, 2, 3], fileName: "A.pdf", contentType: "application/zip");

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UploadAsync(input));

        exception.Code.ShouldBe(DocumentAIErrorCodes.Document.UnsupportedFileType);
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

        exception.Code.ShouldBe(DocumentAIErrorCodes.Document.UnsupportedFileType);
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

        exception.Code.ShouldBe(DocumentAIErrorCodes.Document.FileTooLarge);
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

            exception.Code.ShouldBe(DocumentAIErrorCodes.Document.FileTooLarge);
        }
        finally
        {
            DocumentConsts.MaxUploadFileBytes = original;
        }
    }

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
