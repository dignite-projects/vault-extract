using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentIdentifierIndexTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Repository is the only DB-touching dep — substitute it so tests are pure
        // behavioral checks (idempotency, normalization, validation) rather than EF integration.
        context.Services.AddSingleton(Substitute.For<IDocumentIdentifierRepository>());
    }
}

public class DocumentIdentifierIndex_Tests
    : PaperbaseApplicationTestBase<DocumentIdentifierIndexTestModule>
{
    private readonly IDocumentIdentifierIndex _index;
    private readonly IDocumentIdentifierRepository _repository;
    private readonly ICurrentTenant _currentTenant;

    public DocumentIdentifierIndex_Tests()
    {
        _index = GetRequiredService<IDocumentIdentifierIndex>();
        _repository = GetRequiredService<IDocumentIdentifierRepository>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    [Fact]
    public async Task RegisterAsync_Should_Insert_When_Not_Exists()
    {
        var documentId = Guid.NewGuid();
        _repository
            .ExistsAsync(documentId, "ContractNumber", "HT-2024-001", Arg.Any<CancellationToken>())
            .Returns(false);

        await _index.RegisterAsync(documentId, "ContractNumber", "HT-2024-001");

        await _repository.Received(1).InsertAsync(
            Arg.Is<DocumentIdentifier>(e =>
                e.DocumentId == documentId
                && e.IdentifierType == "ContractNumber"
                && e.IdentifierValue == "HT-2024-001"),
            autoSave: true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_Should_Be_Idempotent_When_Already_Exists()
    {
        var documentId = Guid.NewGuid();
        _repository
            .ExistsAsync(documentId, "ContractNumber", "HT-2024-001", Arg.Any<CancellationToken>())
            .Returns(true);

        await _index.RegisterAsync(documentId, "ContractNumber", "HT-2024-001");

        await _repository.DidNotReceive().InsertAsync(
            Arg.Any<DocumentIdentifier>(),
            autoSave: Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_Should_Trim_Inputs_Before_ExistsAsync()
    {
        var documentId = Guid.NewGuid();
        _repository
            .ExistsAsync(documentId, "ContractNumber", "HT-2024-001", Arg.Any<CancellationToken>())
            .Returns(true);

        // Surrounding whitespace must not produce false-negatives in idempotency check.
        await _index.RegisterAsync(documentId, "  ContractNumber  ", " HT-2024-001\n");

        await _repository.Received(1).ExistsAsync(
            documentId, "ContractNumber", "HT-2024-001", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_Should_Reject_Empty_Document_Id()
    {
        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _index.RegisterAsync(Guid.Empty, "ContractNumber", "HT-2024-001"));

        exception.Code.ShouldBe(PaperbaseErrorCodes.DocumentIdentifierDocumentIdRequired);
    }

    [Fact]
    public async Task RegisterAsync_Should_Reject_Blank_Type()
    {
        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _index.RegisterAsync(Guid.NewGuid(), "  ", "HT-2024-001"));

        exception.Code.ShouldBe(PaperbaseErrorCodes.DocumentIdentifierTypeRequired);
    }

    [Fact]
    public async Task RegisterAsync_Should_Reject_Blank_Value()
    {
        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _index.RegisterAsync(Guid.NewGuid(), "ContractNumber", " "));

        exception.Code.ShouldBe(PaperbaseErrorCodes.DocumentIdentifierValueRequired);
    }

    [Fact]
    public async Task RegisterAsync_Should_Stamp_Current_Tenant_Id_On_Inserted_Entity()
    {
        var documentId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        _repository
            .ExistsAsync(documentId, "ContractNumber", "HT-2024-001", Arg.Any<CancellationToken>())
            .Returns(false);

        DocumentIdentifier? captured = null;
        await _repository.InsertAsync(
            Arg.Do<DocumentIdentifier>(e => captured = e),
            autoSave: true,
            Arg.Any<CancellationToken>());

        using (_currentTenant.Change(tenantId))
        {
            await _index.RegisterAsync(documentId, "ContractNumber", "HT-2024-001");
        }

        captured.ShouldNotBeNull();
        captured!.TenantId.ShouldBe(tenantId);
    }

    [Fact]
    public async Task FindDocumentsAsync_Should_Trim_Inputs_And_Delegate_To_Repository()
    {
        var expected = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _repository
            .FindDocumentIdsAsync("ContractNumber", "HT-2024-001", Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _index.FindDocumentsAsync(" ContractNumber ", "HT-2024-001\t");

        result.ShouldBe(expected);
        await _repository.Received(1).FindDocumentIdsAsync(
            "ContractNumber", "HT-2024-001", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindDocumentsAsync_Should_Reject_Blank_Type()
    {
        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _index.FindDocumentsAsync(" ", "HT-2024-001"));

        exception.Code.ShouldBe(PaperbaseErrorCodes.DocumentIdentifierTypeRequired);
    }

    [Fact]
    public async Task FindDocumentsAsync_Should_Reject_Blank_Value()
    {
        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _index.FindDocumentsAsync("ContractNumber", "  "));

        exception.Code.ShouldBe(PaperbaseErrorCodes.DocumentIdentifierValueRequired);
    }

    [Fact]
    public async Task RemoveByDocumentIdAsync_Should_Delegate_To_Repository()
    {
        var documentId = Guid.NewGuid();

        await _index.RemoveByDocumentIdAsync(documentId);

        await _repository.Received(1).RemoveByDocumentIdAsync(documentId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveByDocumentIdAsync_Should_Reject_Empty_Document_Id()
    {
        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _index.RemoveByDocumentIdAsync(Guid.Empty));

        exception.Code.ShouldBe(PaperbaseErrorCodes.DocumentIdentifierDocumentIdRequired);
    }
}
