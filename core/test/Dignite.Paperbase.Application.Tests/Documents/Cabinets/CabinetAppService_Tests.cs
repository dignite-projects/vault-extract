using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents.Cabinets;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class CabinetAppServiceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
    }
}

/// <summary>
/// <see cref="CabinetAppService.DeleteAsync"/> 行为测试——核心是 Codex adversarial review (#194) 指出的
/// 删柜归属处理：删柜必须原子清空该柜文档的 CabinetId（真正 unfile），否则文档悬空指向已删柜。
/// </summary>
public class CabinetAppService_Tests : PaperbaseApplicationTestBase<CabinetAppServiceTestModule>
{
    private readonly ICabinetAppService _appService;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IDocumentRepository _documentRepository;

    public CabinetAppService_Tests()
    {
        _appService = GetRequiredService<ICabinetAppService>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
    }

    [Fact]
    public async Task DeleteAsync_Unfiles_All_Referencing_Documents_Before_Removing_Cabinet()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal");
        _cabinetRepository.GetAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(cabinet);

        var doc1 = CreateDocumentInCabinet(cabinet.Id);
        var doc2 = CreateDocumentInCabinet(cabinet.Id);
        _documentRepository.GetListAsync(
                Arg.Any<Expression<Func<Document, bool>>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Document> { doc1, doc2 });

        await _appService.DeleteAsync(cabinet.Id);

        // 归属被清空（回退未归类）——无悬空指向已删柜，重建同名柜不会误纳老文档。
        doc1.CabinetId.ShouldBeNull();
        doc2.CabinetId.ShouldBeNull();
        await _documentRepository.Received(1).UpdateManyAsync(
            Arg.Any<IEnumerable<Document>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _cabinetRepository.Received(1).DeleteAsync(cabinet, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_Skips_Unfile_When_No_Documents_Reference_Cabinet()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Empty");
        _cabinetRepository.GetAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(cabinet);
        _documentRepository.GetListAsync(
                Arg.Any<Expression<Func<Document, bool>>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Document>());

        await _appService.DeleteAsync(cabinet.Id);

        await _documentRepository.DidNotReceive().UpdateManyAsync(
            Arg.Any<IEnumerable<Document>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _cabinetRepository.Received(1).DeleteAsync(cabinet, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_Throws_EntityNotFound_For_Cross_Layer_Cabinet()
    {
        // 跨层防御：当前层（Host，CurrentTenant.Id IS NULL）尝试删一个租户柜 → EntityNotFound，且不删任何东西。
        var tenantCabinet = new Cabinet(Guid.NewGuid(), Guid.NewGuid(), "TenantOwned");
        _cabinetRepository.GetAsync(tenantCabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(tenantCabinet);

        await Should.ThrowAsync<EntityNotFoundException>(async () =>
            await _appService.DeleteAsync(tenantCabinet.Id));

        await _documentRepository.DidNotReceive().UpdateManyAsync(
            Arg.Any<IEnumerable<Document>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _cabinetRepository.DidNotReceive().DeleteAsync(
            Arg.Any<Cabinet>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static Document CreateDocumentInCabinet(Guid cabinetId)
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: null,
            originalFileBlobName: $"blobs/{Guid.NewGuid():N}.pdf",
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"),
            cabinetId: cabinetId);
    }
}
