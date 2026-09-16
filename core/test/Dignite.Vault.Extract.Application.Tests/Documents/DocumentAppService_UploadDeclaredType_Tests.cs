using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions.Resources;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class DocumentAppServiceUploadDeclaredTypeTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Replace the always-allow IAuthorizationService with a controllable grant set (same pattern as
        // SchemaReadAuthorization_Tests / GrantSetAuthorizationService), so the "caller lacks
        // ConfirmClassification" acceptance scenario can actually deny it. Default grant covers both
        // permissions; individual tests narrow it.
        context.Services.AddSingleton(sp => new GrantSetAuthorizationService(sp)
        {
            Granted = new HashSet<string>
            {
                VaultExtractPermissions.Documents.Upload,
                VaultExtractPermissions.Documents.ConfirmClassification
            }
        });
        context.Services.RemoveAll<IAuthorizationService>();
        context.Services.RemoveAll<IAbpAuthorizationService>();
        context.Services.AddSingleton<IAuthorizationService>(sp => sp.GetRequiredService<GrantSetAuthorizationService>());
        context.Services.AddSingleton<IAbpAuthorizationService>(sp => sp.GetRequiredService<GrantSetAuthorizationService>());

        // #629: the per-type half of UploadAsync's OR runs through ABP's real ResourcePermissionChecker and its
        // real user / role value providers; only the grant TABLE is faked. Replacing NullResourcePermissionStore
        // (which answers false to everything) is what makes a granted case expressible at all.
        context.Services.AddSingleton<InMemoryResourcePermissionStore>();
        context.Services.RemoveAll<IResourcePermissionStore>();
        context.Services.AddSingleton<IResourcePermissionStore>(sp => sp.GetRequiredService<InMemoryResourcePermissionStore>());

        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// #623: covers UploadAsync's declared-DocumentTypeId acceptance list (the Upload-time half only — the
/// Parse-cascade completion of the Classification stage is covered by
/// <c>DocumentParseBackgroundJob_DeclaredType_Tests</c>, which needs a different dependency set).
/// <para>
/// #629 turns that list into a per-type rule: <c>ConfirmClassification</c> (every type of the layer) <b>or</b> a
/// resource <c>Upload</c> grant on the declared type, with untyped upload now requiring
/// <c>ConfirmClassification</c> as well. The resource half runs through ABP's real checker and value providers
/// against <see cref="InMemoryResourcePermissionStore"/>; see <see cref="GrantSetAuthorizationService"/>.
/// </para>
/// </summary>
public class DocumentAppService_UploadDeclaredType_Tests
    : VaultExtractApplicationTestBase<DocumentAppServiceUploadDeclaredTypeTestModule>
{
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string RoleName = "invoice-uploader";

    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldRepository _fieldRepository;
    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly GrantSetAuthorizationService _authorization;
    private readonly InMemoryResourcePermissionStore _resourcePermissionStore;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public DocumentAppService_UploadDeclaredType_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldRepository = GetRequiredService<IFieldRepository>();
        _blobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _authorization = GetRequiredService<GrantSetAuthorizationService>();
        _resourcePermissionStore = GetRequiredService<InMemoryResourcePermissionStore>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();

        // UploadAsync precondition fail-fast check (mirrors DocumentAppService_Delete_Tests).
        _documentTypeRepository.GetCountAsync(Arg.Any<CancellationToken>()).Returns(1L);
        // MapToDtoAsync's ResolveReferenceMapsAsync always looks up field definitions for a document's type;
        // harmless no-op default for tests whose document ends up with a DocumentTypeId set.
        _fieldRepository.GetListAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<Field, bool>>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private void Grant(params string[] permissions) => _authorization.Granted = new HashSet<string>(permissions);

    /// <summary>Registers a resolvable DocumentType, plus the reference lookup MapToDtoAsync does afterwards.</summary>
    private DocumentType StubType(string typeCode)
    {
        var type = new DocumentType(Guid.NewGuid(), null, typeCode, typeCode);
        _documentTypeRepository.FindAsync(type.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(type);
        _documentTypeRepository.GetListAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<DocumentType, bool>>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns([type]);
        return type;
    }

    private void GrantResource(string providerName, string providerKey, Guid documentTypeId)
    {
        _resourcePermissionStore.Grant(
            VaultExtractPermissions.DocumentTypes.Resources.Upload,
            VaultExtractPermissions.DocumentTypes.Resources.Name,
            documentTypeId.ToString(),
            providerName,
            providerKey);
    }

    // The claim shapes ABP's resource permission value providers read: AbpClaimTypes.UserId for "U",
    // AbpClaimTypes.Role for "R". UserName is carried too because UploadAsync stamps CurrentUser.UserName into
    // FileOrigin, which rejects an empty one — an ambient principal has to look like a real caller, not only
    // like a permission subject.
    private static ClaimsPrincipal PrincipalWithUser()
        => new(new ClaimsIdentity(
            [
                new Claim(AbpClaimTypes.UserId, UserId.ToString()),
                new Claim(AbpClaimTypes.UserName, "test-user")
            ],
            authenticationType: "ApplicationTest"));

    private static ClaimsPrincipal PrincipalWithUserAndRole()
        => new(new ClaimsIdentity(
            [
                new Claim(AbpClaimTypes.UserId, UserId.ToString()),
                new Claim(AbpClaimTypes.UserName, "test-user"),
                new Claim(AbpClaimTypes.Role, RoleName)
            ],
            authenticationType: "ApplicationTest"));

    [Fact]
    public async Task UploadAsync_With_Valid_DocumentTypeId_Declares_The_Type_As_Confirmed()
    {
        var type = new DocumentType(Guid.NewGuid(), null, "invoice.general", "Invoice");
        _documentTypeRepository.FindAsync(type.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(type);
        // MapToDtoAsync's ResolveReferenceMapsAsync resolves DocumentTypeCode for the returned DTO once
        // DocumentTypeId is set; stub the predicate-based lookup it uses.
        _documentTypeRepository.GetListAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<DocumentType, bool>>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns([type]);

        var input = CreateUploadInput([1, 2, 3]);
        input.DocumentTypeId = type.Id;

        await _appService.UploadAsync(input);

        // #623 decision 1: declaring a type is equivalent to an operator confirmation -- confidence 1.0,
        // Confirmed disposition, no UnresolvedClassification review reason -- applied synchronously at
        // upload, before any pipeline has run.
        await _documentRepository.Received(1).InsertAsync(
            Arg.Is<Document>(d =>
                d.DocumentTypeId == type.Id &&
                d.ClassificationConfidence == 1.0 &&
                d.ReviewDisposition == DocumentReviewDisposition.Confirmed &&
                (d.ReviewReasons & DocumentReviewReasons.UnresolvedClassification) == DocumentReviewReasons.None),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());

        // Parse is still enqueued exactly as before -- the LLM classification short-circuit happens later,
        // in the Parse job's cascade branch (#623 decision 2), not at upload time.
        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Any<DocumentParseJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task UploadAsync_Throws_AbpAuthorizationException_When_Caller_Lacks_ConfirmClassification()
    {
        // #629 reordered the block so existence is validated before authorization; the type therefore has to
        // resolve for this test to still be about the permission. Before #629 the id resolved to null and the
        // permission check ran first, so no stub was needed.
        var type = StubType("invoice.general");
        Grant(VaultExtractPermissions.Documents.Upload); // Upload only, no ConfirmClassification, no resource grant.

        var input = CreateUploadInput([1, 2, 3]);
        input.DocumentTypeId = type.Id;

        await Should.ThrowAsync<AbpAuthorizationException>(() => _appService.UploadAsync(input));

        await _documentRepository.DidNotReceive().InsertAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_Throws_EntityNotFoundException_When_DocumentTypeId_Does_Not_Resolve()
    {
        // No FindAsync stub for this id -> mock default returns null, representing an unknown id or a
        // cross-layer id filtered out by the ambient IMultiTenant filter.
        var input = CreateUploadInput([1, 2, 3]);
        input.DocumentTypeId = Guid.NewGuid();

        await Should.ThrowAsync<EntityNotFoundException>(() => _appService.UploadAsync(input));

        await _documentRepository.DidNotReceive().InsertAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_Without_DocumentTypeId_Behaves_Exactly_As_Before()
    {
        await _appService.UploadAsync(CreateUploadInput([1, 2, 3]));

        await _documentRepository.Received(1).InsertAsync(
            Arg.Is<Document>(d =>
                d.DocumentTypeId == null &&
                d.ClassificationConfidence == 0 &&
                d.ReviewDisposition == DocumentReviewDisposition.NotReviewed),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Any<DocumentParseJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    // ---- #629: the per-type upload grant ----

    [Fact]
    public async Task UploadAsync_With_A_User_Resource_Grant_Declares_The_Type_Without_ConfirmClassification()
    {
        var type = StubType("invoice.general");
        Grant(VaultExtractPermissions.Documents.Upload);
        GrantResource(UserResourcePermissionValueProvider.ProviderName, UserId.ToString(), type.Id);

        var input = CreateUploadInput([1, 2, 3]);
        input.DocumentTypeId = type.Id;

        using (_principalAccessor.Change(PrincipalWithUser()))
        {
            await _appService.UploadAsync(input);
        }

        // The grant is exactly the delegation of the ConfirmClassification decision for this one type, so the
        // outcome is #623's verbatim: confidence 1.0, Confirmed, no UnresolvedClassification.
        await _documentRepository.Received(1).InsertAsync(
            Arg.Is<Document>(d =>
                d.DocumentTypeId == type.Id &&
                d.ClassificationConfidence == 1.0 &&
                d.ReviewDisposition == DocumentReviewDisposition.Confirmed &&
                (d.ReviewReasons & DocumentReviewReasons.UnresolvedClassification) == DocumentReviewReasons.None),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_With_A_Role_Resource_Grant_Declares_The_Type()
    {
        var type = StubType("invoice.general");
        Grant(VaultExtractPermissions.Documents.Upload);
        GrantResource(RoleResourcePermissionValueProvider.ProviderName, RoleName, type.Id);

        var input = CreateUploadInput([1, 2, 3]);
        input.DocumentTypeId = type.Id;

        using (_principalAccessor.Change(PrincipalWithUserAndRole()))
        {
            await _appService.UploadAsync(input);
        }

        await _documentRepository.Received(1).InsertAsync(
            Arg.Is<Document>(d => d.DocumentTypeId == type.Id && d.ClassificationConfidence == 1.0),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_Throws_When_The_Resource_Grant_Is_On_A_Different_Type()
    {
        var declared = StubType("invoice.general");
        var granted = StubType("contract.general");
        Grant(VaultExtractPermissions.Documents.Upload);
        GrantResource(UserResourcePermissionValueProvider.ProviderName, UserId.ToString(), granted.Id);

        var input = CreateUploadInput([1, 2, 3]);
        input.DocumentTypeId = declared.Id;

        using (_principalAccessor.Change(PrincipalWithUser()))
        {
            await Should.ThrowAsync<AbpAuthorizationException>(() => _appService.UploadAsync(input));
        }

        await _documentRepository.DidNotReceive().InsertAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_Untyped_Throws_For_An_Upload_Only_Caller()
    {
        // #629 decision 2, the deliberate behaviour change: leaving DocumentTypeId null used to be the
        // unprivileged path. It now requires ConfirmClassification, because otherwise the per-type ACL is
        // bypassable by letting the LLM pick the type.
        Grant(VaultExtractPermissions.Documents.Upload);

        using (_principalAccessor.Change(PrincipalWithUser()))
        {
            await Should.ThrowAsync<AbpAuthorizationException>(
                () => _appService.UploadAsync(CreateUploadInput([1, 2, 3])));
        }

        await _documentRepository.DidNotReceive().InsertAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_Untyped_Throws_Even_With_A_Resource_Grant_On_Every_Type()
    {
        // A resource grant is per type; it says nothing about letting the channel choose the type. Without this
        // the "bypass by uploading untyped" hole would reopen for exactly the callers the grant was meant to
        // constrain.
        var type = StubType("invoice.general");
        Grant(VaultExtractPermissions.Documents.Upload);
        GrantResource(UserResourcePermissionValueProvider.ProviderName, UserId.ToString(), type.Id);

        using (_principalAccessor.Change(PrincipalWithUser()))
        {
            await Should.ThrowAsync<AbpAuthorizationException>(
                () => _appService.UploadAsync(CreateUploadInput([1, 2, 3])));
        }
    }

    [Fact]
    public async Task UploadAsync_Does_Not_Consult_The_Grant_Store_When_The_Type_Does_Not_Resolve()
    {
        // Acceptance case "a grant on a Host-layer type id does not authorize a tenant caller": the ambient
        // IMultiTenant filter makes FindAsync return null (here: no stub for the id), and existence is checked
        // first, so the permission layer is never reached. Asserting the store was untouched is what proves the
        // ordering rather than merely the outcome.
        var hostTypeId = Guid.NewGuid();
        Grant(VaultExtractPermissions.Documents.Upload);
        GrantResource(UserResourcePermissionValueProvider.ProviderName, UserId.ToString(), hostTypeId);
        _resourcePermissionStore.ResetLookupCount();

        var input = CreateUploadInput([1, 2, 3]);
        input.DocumentTypeId = hostTypeId;

        using (_principalAccessor.Change(PrincipalWithUser()))
        {
            await Should.ThrowAsync<EntityNotFoundException>(() => _appService.UploadAsync(input));
        }

        _resourcePermissionStore.LookupCount.ShouldBe(0);
    }

    [Fact]
    public void DeclareDocumentType_Throws_When_DocumentTypeId_Already_Set()
    {
        var doc = CreateDocument();
        InvokeDeclareDocumentType(doc, Guid.NewGuid());

        var exception = Should.Throw<TargetInvocationException>(
            () => InvokeDeclareDocumentType(doc, Guid.NewGuid()));

        exception.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void DeclareDocumentType_Throws_When_Markdown_Already_Set()
    {
        var doc = CreateDocument();
        typeof(Document).GetProperty(nameof(Document.Markdown))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(doc, ["# Already extracted"]);

        var exception = Should.Throw<TargetInvocationException>(
            () => InvokeDeclareDocumentType(doc, Guid.NewGuid()));

        exception.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void RetractDeclaredType_Throws_When_No_Type_Is_Declared()
    {
        var doc = CreateDocument();

        var exception = Should.Throw<TargetInvocationException>(() => InvokeRetractDeclaredType(doc));

        exception.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void RetractDeclaredType_Resets_Declared_State_Back_To_Not_Reviewed()
    {
        var doc = CreateDocument();
        InvokeDeclareDocumentType(doc, Guid.NewGuid());

        InvokeRetractDeclaredType(doc);

        doc.DocumentTypeId.ShouldBeNull();
        doc.ClassificationConfidence.ShouldBe(0d);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);
        doc.ReviewReasons.ShouldBe(DocumentReviewReasons.None);
    }

    /// <summary>
    /// Strengthened guard (code review on #623, 2026-09-05): <see cref="Document.RetractDeclaredType"/> is exposed
    /// publicly via <see cref="DocumentPipelineRunManager.RetractDeclaredType"/>, so it must refuse anything that is
    /// not the exact upload-declared, never-classified signature -- not merely "has a type". A document classified
    /// through the ordinary automatic path (<c>ApplyAutomaticClassificationResult</c>) carries a type with
    /// <see cref="DocumentReviewDisposition.NotReviewed"/>, not Confirmed, and must not be retractable.
    /// </summary>
    [Fact]
    public void RetractDeclaredType_Throws_When_ReviewDisposition_Is_Not_Confirmed()
    {
        var doc = CreateDocument();
        InvokeApplyAutomaticClassificationResult(doc, Guid.NewGuid(), 0.95);
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.NotReviewed);

        var exception = Should.Throw<TargetInvocationException>(() => InvokeRetractDeclaredType(doc));

        exception.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    /// <summary>
    /// Same strengthened guard, the other axis: <c>ConfirmClassification</c> also sets ReviewDisposition=Confirmed +
    /// confidence 1.0 -- identical to the upload-declared signature on those two fields alone -- so once its cascade
    /// field extraction has actually produced values, the document carries a non-empty <see cref="Document.FlexFields"/>
    /// bag that the upload-declared, never-classified signature never has (<see cref="Document.DeclareDocumentType"/>
    /// deliberately never touches <c>FlexFields</c>). That is the signal this guard leans on to refuse it.
    /// </summary>
    [Fact]
    public void RetractDeclaredType_Throws_When_Type_Was_Operator_Confirmed_And_Fields_Were_Extracted()
    {
        var doc = CreateDocument();
        InvokeConfirmClassification(doc, Guid.NewGuid());
        doc.ReviewDisposition.ShouldBe(DocumentReviewDisposition.Confirmed);
        doc.ClassificationConfidence.ShouldBe(1.0);
        // Simulates the #527 §8 field-extraction cascade having already completed.
        doc.SetFlexFields(new Dictionary<string, object?> { ["amount"] = 100m });

        var exception = Should.Throw<TargetInvocationException>(() => InvokeRetractDeclaredType(doc));

        exception.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    private static void InvokeApplyAutomaticClassificationResult(Document document, Guid documentTypeId, double confidence)
    {
        typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(document, [documentTypeId, confidence]);
    }

    private static void InvokeConfirmClassification(Document document, Guid documentTypeId)
    {
        typeof(Document)
            .GetMethod("ConfirmClassification", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(document, [documentTypeId]);
    }

    private static void InvokeDeclareDocumentType(Document document, Guid documentTypeId)
    {
        typeof(Document)
            .GetMethod("DeclareDocumentType", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(document, [documentTypeId]);
    }

    private static void InvokeRetractDeclaredType(Document document)
    {
        typeof(Document)
            .GetMethod("RetractDeclaredType", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(document, null);
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
