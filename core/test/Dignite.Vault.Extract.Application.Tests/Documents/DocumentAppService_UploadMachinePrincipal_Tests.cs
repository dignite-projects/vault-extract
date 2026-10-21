using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using NSubstitute;
using Shouldly;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// A client-credentials principal (a client id, no <c>sub</c>, no user name) calling <c>UploadAsync</c>.
/// <c>FileOrigin</c> refuses a blank <c>UploadedByUserName</c>, and the upload used to discover that only
/// <b>after</b> the bytes had been written to the blob container — a 500 that left an orphaned blob no
/// <c>Document</c> row points at. Whatever an upload refuses, it must refuse before the blob write.
/// </summary>
public class DocumentAppService_UploadMachinePrincipal_Tests
    : VaultExtractApplicationTestBase<DocumentAppServiceUploadDeclaredTypeTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public DocumentAppService_UploadMachinePrincipal_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();

        // UploadAsync precondition fail-fast check (mirrors DocumentAppService_UploadDeclaredType_Tests).
        GetRequiredService<IDocumentTypeRepository>()
            .GetCountAsync(Arg.Any<CancellationToken>()).Returns(1L);
    }

    [Fact]
    public async Task UploadAsync_By_A_Principal_Without_A_User_Name_Writes_No_Blob_When_It_Fails()
    {
        using (_principalAccessor.Change(MachinePrincipal()))
        {
            await Should.ThrowAsync<ArgumentException>(() => _appService.UploadAsync(CreateUploadInput([1, 2, 3])));
        }

        await _blobContainer.DidNotReceiveWithAnyArgs().SaveAsync(default!, default(Stream)!, default, default);
        await _documentRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
    }

    /// <summary>A client-credentials principal: a client id, and deliberately no user id or user name.</summary>
    private static ClaimsPrincipal MachinePrincipal()
        => new(new ClaimsIdentity(
            [new Claim(AbpClaimTypes.ClientId, "machine-upload-test")],
            authenticationType: "ApplicationTest"));

    private static UploadDocumentInput CreateUploadInput(byte[] bytes)
        => new()
        {
            File = new RemoteStreamContent(new MemoryStream(bytes), "A.pdf", "application/pdf", disposeStream: true)
        };
}
