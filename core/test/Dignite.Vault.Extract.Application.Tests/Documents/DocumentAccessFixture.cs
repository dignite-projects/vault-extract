using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Pipelines;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Authorization.Permissions.Resources;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Volo.Abp.Security.Claims;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Host for the #635 facts: the same seam <c>DocumentTypeAccessTestModule</c> uses (a controllable grant set for
/// the module-wide arm, ABP's <b>real</b> resource-permission checker and value providers over an in-memory grant
/// table for the per-type arm) plus the pipeline-run fake, so the retry family and the lifecycle re-derivation
/// inside the edit family actually run instead of failing on an unstubbed repository.
/// <para>
/// <see cref="CountingResourcePermissionChecker"/> wraps ABP's own checker so a fact can assert how many
/// multi-name grant checks one request performs — the cost claim in #635 decision 4, which no behavioural test
/// would notice regressing.
/// </para>
/// </summary>
[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class DocumentAccessTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(sp => new GrantSetAuthorizationService(sp));
        context.Services.RemoveAll<IAuthorizationService>();
        context.Services.RemoveAll<IAbpAuthorizationService>();
        context.Services.AddSingleton<IAuthorizationService>(sp => sp.GetRequiredService<GrantSetAuthorizationService>());
        context.Services.AddSingleton<IAbpAuthorizationService>(sp => sp.GetRequiredService<GrantSetAuthorizationService>());

        context.Services.AddSingleton<InMemoryResourcePermissionStore>();
        context.Services.RemoveAll<IResourcePermissionStore>();
        context.Services.AddSingleton<IResourcePermissionStore>(sp => sp.GetRequiredService<InMemoryResourcePermissionStore>());

        // The scoped grant memo, plus one extra reason to forget: this host changes what a principal is granted
        // inside one scope, which no request does. See TestDocumentTypeGrantMap.
        context.Services.UseTestGrantMap();

        // Decorate rather than replace: ABP's real ResourcePermissionChecker still does the deciding, and the
        // wrapper only counts. A hand-written stand-in would have let the cost claim assert itself.
        context.Services.AddSingleton<ResourcePermissionCheckCounter>();
        context.Services.RemoveAll<IResourcePermissionChecker>();
        context.Services.AddTransient<IResourcePermissionChecker>(sp => new CountingResourcePermissionChecker(
            sp.GetRequiredService<ResourcePermissionChecker>(),
            sp.GetRequiredService<ResourcePermissionCheckCounter>()));

        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());
    }
}

/// <summary>Shared, resettable tally of multi-name resource-permission checks.</summary>
public sealed class ResourcePermissionCheckCounter
{
    /// <summary>Calls to the multi-name overload — the one <c>DocumentTypeGrantMap</c> uses, one per type.</summary>
    public int MultiNameChecks { get; set; }

    /// <summary>Calls to the single-name overload. Nothing on the #635 path should use it.</summary>
    public int SingleNameChecks { get; set; }

    public void Reset()
    {
        MultiNameChecks = 0;
        SingleNameChecks = 0;
    }
}

/// <summary>Pass-through decorator over ABP's <see cref="ResourcePermissionChecker"/> that tallies each call.</summary>
public sealed class CountingResourcePermissionChecker : IResourcePermissionChecker
{
    private readonly IResourcePermissionChecker _inner;
    private readonly ResourcePermissionCheckCounter _counter;

    public CountingResourcePermissionChecker(IResourcePermissionChecker inner, ResourcePermissionCheckCounter counter)
    {
        _inner = inner;
        _counter = counter;
    }

    public Task<bool> IsGrantedAsync(string name, string resourceName, string resourceKey)
    {
        _counter.SingleNameChecks++;
        return _inner.IsGrantedAsync(name, resourceName, resourceKey);
    }

    public Task<bool> IsGrantedAsync(ClaimsPrincipal? claimsPrincipal, string name, string resourceName, string resourceKey)
    {
        _counter.SingleNameChecks++;
        return _inner.IsGrantedAsync(claimsPrincipal, name, resourceName, resourceKey);
    }

    public Task<MultiplePermissionGrantResult> IsGrantedAsync(string[] names, string resourceName, string resourceKey)
    {
        _counter.MultiNameChecks++;
        return _inner.IsGrantedAsync(names, resourceName, resourceKey);
    }

    public Task<MultiplePermissionGrantResult> IsGrantedAsync(
        ClaimsPrincipal? claimsPrincipal, string[] names, string resourceName, string resourceKey)
    {
        _counter.MultiNameChecks++;
        return _inner.IsGrantedAsync(claimsPrincipal, names, resourceName, resourceKey);
    }
}

/// <summary>
/// The arrangement every #635 fact shares: two document types in the layer, a principal that can be switched
/// between "the uploader" and "somebody else", and documents stubbed onto both repository loaders.
/// </summary>
public abstract class DocumentAccessTestBase : VaultExtractApplicationTestBase<DocumentAccessTestModule>
{
    /// <summary>The uploader. Documents created with <c>creatorId: OwnerId</c> belong to this principal.</summary>
    protected static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-000000000635");

    /// <summary>A second real user, holding whatever the fact grants and owning nothing.</summary>
    protected static readonly Guid StrangerId = Guid.Parse("22222222-2222-2222-2222-000000000635");

    protected readonly IDocumentAppService AppService;
    protected readonly IDocumentRepository DocumentRepository;
    protected readonly IDocumentTypeRepository DocumentTypeRepository;
    protected readonly IFieldRepository FieldRepository;
    protected readonly IBlobContainer<VaultExtractDocumentContainer> BlobContainer;
    protected readonly GrantSetAuthorizationService Authorization;
    protected readonly InMemoryResourcePermissionStore ResourcePermissionStore;
    protected readonly ResourcePermissionCheckCounter CheckCounter;
    protected readonly ICurrentPrincipalAccessor PrincipalAccessor;

    protected readonly DocumentType TypeA = new(Guid.NewGuid(), null, "own.a", "Type A");
    protected readonly DocumentType TypeB = new(Guid.NewGuid(), null, "own.b", "Type B");

    /// <summary>
    /// Two more types nothing is granted on, so the layer is bigger than the set any fact reaches. The cost facts
    /// need it: with only two types, "one grant check per type" and "one grant check, full stop" are the same
    /// number for a two-type page and a per-type sweep would pass a per-document assertion.
    /// </summary>
    protected readonly DocumentType TypeC = new(Guid.NewGuid(), null, "own.c", "Type C");
    protected readonly DocumentType TypeD = new(Guid.NewGuid(), null, "own.d", "Type D");

    /// <summary>Every type of the layer, in the order the sweep enumerates them.</summary>
    protected DocumentType[] LayerTypes => [TypeA, TypeB, TypeC, TypeD];

    protected DocumentAccessTestBase()
    {
        AppService = GetRequiredService<IDocumentAppService>();
        DocumentRepository = GetRequiredService<IDocumentRepository>();
        DocumentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        FieldRepository = GetRequiredService<IFieldRepository>();
        BlobContainer = GetRequiredService<IBlobContainer<VaultExtractDocumentContainer>>();
        Authorization = GetRequiredService<GrantSetAuthorizationService>();
        ResourcePermissionStore = GetRequiredService<InMemoryResourcePermissionStore>();
        CheckCounter = GetRequiredService<ResourcePermissionCheckCounter>();
        PrincipalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();

        DocumentTypeRepository.GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<DocumentType>(LayerTypes));
        DocumentTypeRepository.GetListAsync(
                Arg.Any<Expression<Func<DocumentType, bool>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.Arg<Expression<Func<DocumentType, bool>>>().Compile();
                return new List<DocumentType>(LayerTypes.Where(predicate));
            });
        DocumentTypeRepository.FindByTypeCodeAsync(TypeA.TypeCode, Arg.Any<CancellationToken>()).Returns(TypeA);
        DocumentTypeRepository.FindByTypeCodeAsync(TypeB.TypeCode, Arg.Any<CancellationToken>()).Returns(TypeB);
        DocumentTypeRepository.FindAsync(TypeA.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(TypeA);
        DocumentTypeRepository.FindAsync(TypeB.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(TypeB);
        DocumentTypeRepository.GetCountAsync(Arg.Any<CancellationToken>()).Returns(LayerTypes.Length);

        FieldRepository.GetListAsync(
                Arg.Any<Expression<Func<Field, bool>>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([]);
        FieldRepository.GetListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    // ---- grants ----

    protected void Grant(params string[] permissions) => Authorization.Granted = new HashSet<string>(permissions);

    protected void GrantEntryOnly() => Grant(VaultExtractPermissions.Documents.Default);

    protected void GrantResource(string permissionName, Guid documentTypeId, Guid? userId = null)
    {
        ResourcePermissionStore.Grant(
            permissionName,
            VaultExtractResourcePermissions.Name,
            documentTypeId.ToString(),
            UserResourcePermissionValueProvider.ProviderName,
            (userId ?? OwnerId).ToString());
    }

    // ---- documents ----

    protected static Document NewDocument(
        Guid? documentTypeId, Guid? creatorId, bool deleted = false, string? markdown = null)
    {
        var document = new Document(
            Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "tester",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "own.pdf"));

        // DocumentTypeId / Markdown have Domain-private setters and CreatorId is ABP's protected audit property;
        // this assembly is outside the Domain's InternalsVisibleTo list, so reflection stands in for the pipeline
        // and for AuditPropertySetter, as the EF / MCP suites already do.
        if (documentTypeId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId.Value);
        }

        if (creatorId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(document, creatorId.Value);
        }

        if (markdown != null)
        {
            typeof(Document).GetProperty(nameof(Document.Markdown))!.SetValue(document, markdown);
        }

        document.IsDeleted = deleted;
        return document;
    }

    /// <summary>Registers a document on both loaders the enforcement points use (lean and field-stage).</summary>
    protected Document StubDocument(
        Guid? documentTypeId, Guid? creatorId, bool deleted = false, string? markdown = null)
    {
        var document = NewDocument(documentTypeId, creatorId, deleted, markdown);
        DocumentRepository.GetAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(document);
        DocumentRepository.FindWithFieldValuesAsync(document.Id, Arg.Any<CancellationToken>()).Returns(document);
        return document;
    }

    protected void StubQueryable(params Document[] documents)
        => DocumentRepository.GetQueryableAsync().Returns(documents.AsQueryable());

    // ---- principal ----

    protected async Task<T> AsUserAsync<T>(Guid userId, Func<Task<T>> action)
    {
        using (PrincipalAccessor.Change(Principal(userId)))
        {
            return await action();
        }
    }

    protected async Task AsUserAsync(Guid userId, Func<Task> action)
    {
        using (PrincipalAccessor.Change(Principal(userId)))
        {
            await action();
        }
    }

    protected Task<T> AsOwnerAsync<T>(Func<Task<T>> action) => AsUserAsync(OwnerId, action);

    protected Task AsOwnerAsync(Func<Task> action) => AsUserAsync(OwnerId, action);

    protected Task<T> AsStrangerAsync<T>(Func<Task<T>> action) => AsUserAsync(StrangerId, action);

    protected Task AsStrangerAsync(Func<Task> action) => AsUserAsync(StrangerId, action);

    /// <summary>
    /// The claims ABP reads: the user id for <c>ICurrentUser.Id</c> and for the resource value provider, plus a
    /// user name because <c>UploadAsync</c> stamps it into the required <c>FileOrigin</c>.
    /// </summary>
    protected static ClaimsPrincipal Principal(Guid userId)
        => new(new ClaimsIdentity(
            [
                new Claim(AbpClaimTypes.UserId, userId.ToString()),
                new Claim(AbpClaimTypes.UserName, "access-test")
            ],
            authenticationType: "ApplicationTest"));
}
