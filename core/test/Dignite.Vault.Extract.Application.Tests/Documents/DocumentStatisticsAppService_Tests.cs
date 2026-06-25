using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(ExtractApplicationTestModule))]
public class DocumentStatisticsAppServiceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
    }
}

/// <summary>
/// Verifies <see cref="IDocumentStatisticsAppService"/> wiring: the repository statistics model is mapped 1:1
/// to <c>DocumentStatisticsDto</c>. The aggregation logic itself is covered against a real database in
/// <c>EfCoreDocumentRepositoryStatistics_Tests</c>.
/// </summary>
public class DocumentStatisticsAppService_Tests
    : ExtractApplicationTestBase<DocumentStatisticsAppServiceTestModule>
{
    private readonly IDocumentStatisticsAppService _appService;
    private readonly IDocumentRepository _documentRepository;

    public DocumentStatisticsAppService_Tests()
    {
        _appService = GetRequiredService<IDocumentStatisticsAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
    }

    [Fact]
    public async Task GetAsync_Maps_Repository_Statistics_To_Dto()
    {
        _documentRepository.GetStatisticsAsync(Arg.Any<CancellationToken>())
            .Returns(new DocumentStatisticsModel
            {
                TotalCount = 7,
                UploadedCount = 2,
                ProcessingCount = 1,
                ReadyCount = 2,
                FailedCount = 2,
                NeedsReviewCount = 1,
                TotalStorageBytes = 1610
            });

        var dto = await _appService.GetAsync();

        dto.TotalCount.ShouldBe(7);
        dto.UploadedCount.ShouldBe(2);
        dto.ProcessingCount.ShouldBe(1);
        dto.ReadyCount.ShouldBe(2);
        dto.FailedCount.ShouldBe(2);
        dto.NeedsReviewCount.ShouldBe(1);
        dto.TotalStorageBytes.ShouldBe(1610);
    }
}
