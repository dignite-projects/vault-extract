using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(ExtractApplicationTestModule))]
public class DocumentPipelineRunAccessorTestModule : AbpModule
{
}

public class DocumentPipelineRunAccessorTests
    : ExtractApplicationTestBase<DocumentPipelineRunAccessorTestModule>
{
    private readonly DocumentPipelineRunAccessor _accessor;
    private readonly DocumentPipelineRunManager _pipelineRunManager;

    public DocumentPipelineRunAccessorTests()
    {
        _accessor = GetRequiredService<DocumentPipelineRunAccessor>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
    }

    [Fact]
    public async Task BeginOrStart_Uses_Pending_Run_For_Expected_Pipeline_When_JobArgs_RunId_Belongs_To_Another_Pipeline()
    {
        var document = CreateDocument();
        var textExtractionRun = await _pipelineRunManager.QueueAsync(document, ExtractPipelines.Parse);
        var classificationRun = await _pipelineRunManager.QueueAsync(document, ExtractPipelines.Classification);

        var actualRun = await _accessor.BeginOrStartAsync(
            document,
            textExtractionRun.Id,
            ExtractPipelines.Classification);

        actualRun.ShouldBe(classificationRun);
        classificationRun.Status.ShouldBe(PipelineRunStatus.Running);
        textExtractionRun.Status.ShouldBe(PipelineRunStatus.Pending);
    }

    [Fact]
    public async Task BeginOrStart_Creates_Replacement_Run_With_JobArgs_RunId_When_Run_Is_Missing()
    {
        var document = CreateDocument();
        var missingRunId = Guid.NewGuid();

        var actualRun = await _accessor.BeginOrStartAsync(
            document,
            missingRunId,
            ExtractPipelines.Classification);

        actualRun.Id.ShouldBe(missingRunId);
        actualRun.PipelineCode.ShouldBe(ExtractPipelines.Classification);
        actualRun.Status.ShouldBe(PipelineRunStatus.Running);
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: Guid.NewGuid(),
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}
