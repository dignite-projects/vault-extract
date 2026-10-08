using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Features;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Features;
using Volo.Abp.Guids;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// <see cref="VaultExtractFeatures.Enable"/> reaches the MCP egress without a line of MCP code: the tools and
/// resources only call the application services through their interfaces, and the gate is on the services'
/// base class. This is the fact that says so, against the real permission pipeline.
/// <para>
/// The principal is <b>granted</b> everything the tools need, so a refusal can only be the feature gate - and
/// the assertion checks the error code rather than the exception type, because a permission refusal is the same
/// <see cref="AbpAuthorizationException"/>. The control fact runs the same calls with the feature on.
/// </para>
/// <para>
/// There is no "refused write is not executed" fact here, and no MCP-level write to make one of: every tool and
/// resource this egress exposes is read-only. The refused-write case is
/// <c>VaultExtractEnableFeature_Tests</c> in Application.Tests.
/// </para>
/// </summary>
public class McpEnableFeatureGate_Tests : McpPermissionPipelineTestBase<McpPermissionPipelineTestModule>
{
    private const string TypeCode = "contract.feature";
    private const string UserProviderName = "U";

    private static readonly Guid ServiceAccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly IDocumentAppService _documentAppService;
    private readonly ICabinetReadAppService _cabinetReadAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IPermissionGrantRepository _permissionGrantRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly TestFeatureValueProvider _testFeatures;

    public McpEnableFeatureGate_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _cabinetReadAppService = GetRequiredService<ICabinetReadAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _permissionGrantRepository = GetRequiredService<IPermissionGrantRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
        _testFeatures = GetRequiredService<TestFeatureValueProvider>();
    }

    [Fact]
    public async Task Tools_serve_a_granted_principal_while_the_feature_is_enabled()
    {
        await SeedAsync();

        using (_principalAccessor.Change(ServiceAccountPrincipal()))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                (await DocumentSearchTool.SearchAsync(_documentAppService, documentTypeCode: TypeCode))
                    .Items.ShouldNotBeEmpty();
                (await CabinetTools.ListAsync(_cabinetReadAppService))
                    .Items.ShouldNotBeEmpty();
            });
        }
    }

    [Fact]
    public async Task Tools_refuse_a_granted_principal_when_the_feature_is_disabled()
    {
        await SeedAsync();
        _testFeatures.Set(VaultExtractFeatures.Enable, "false");

        using (_principalAccessor.Change(ServiceAccountPrincipal()))
        {
            (await Should.ThrowAsync<AbpAuthorizationException>(() => WithUnitOfWorkAsync(() =>
                    DocumentSearchTool.SearchAsync(_documentAppService, documentTypeCode: TypeCode))))
                .Code.ShouldBe(AbpFeatureErrorCodes.AtLeastOneOfTheseFeaturesMustBeEnabled);

            (await Should.ThrowAsync<AbpAuthorizationException>(() => WithUnitOfWorkAsync(() =>
                    CabinetTools.ListAsync(_cabinetReadAppService))))
                .Code.ShouldBe(AbpFeatureErrorCodes.AtLeastOneOfTheseFeaturesMustBeEnabled);
        }
    }

    private async Task SeedAsync()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var typeId = Guid.NewGuid();
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, tenantId: null, TypeCode, "Feature Contract"), autoSave: true);

            var document = new Document(
                Guid.NewGuid(),
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                    uploadedByUserName: "svc",
                    contentType: "application/pdf",
                    contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    fileSize: 1024,
                    originalFileName: "feature.pdf"));
            typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, typeId);
            await _documentRepository.InsertAsync(document, autoSave: true);

            await _cabinetRepository.InsertAsync(
                new Cabinet(Guid.NewGuid(), tenantId: null, "Legal", "Contracts"), autoSave: true);

            // The same pair McpPermissionResolution_Tests grants: entry plus the module-wide read.
            await GrantAsync(VaultExtractPermissions.Documents.Default);
            await GrantAsync(VaultExtractPermissions.Documents.ReadAll);
        });
    }

    private async Task GrantAsync(string permissionName)
    {
        await _permissionGrantRepository.InsertAsync(
            new PermissionGrant(
                _guidGenerator.Create(),
                permissionName,
                UserProviderName,
                ServiceAccountId.ToString(),
                tenantId: null),
            autoSave: true);
    }

    private static ClaimsPrincipal ServiceAccountPrincipal()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(AbpClaimTypes.UserId, ServiceAccountId.ToString()) },
            authenticationType: "IntegrationTest");
        return new ClaimsPrincipal(identity);
    }
}
