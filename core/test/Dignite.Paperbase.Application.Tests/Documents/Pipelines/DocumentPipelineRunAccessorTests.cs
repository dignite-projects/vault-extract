using System;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentPipelineRunAccessorTestModule : AbpModule
{
}

public class DocumentPipelineRunAccessorTests
    : PaperbaseApplicationTestBase<DocumentPipelineRunAccessorTestModule>
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
        var textExtractionRun = await _pipelineRunManager.QueueAsync(document, PaperbasePipelines.TextExtraction);
        var classificationRun = await _pipelineRunManager.QueueAsync(document, PaperbasePipelines.Classification);

        var actualRun = await _accessor.BeginOrStartAsync(
            document,
            textExtractionRun.Id,
            PaperbasePipelines.Classification);

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
            PaperbasePipelines.Classification);

        actualRun.Id.ShouldBe(missingRunId);
        actualRun.PipelineCode.ShouldBe(PaperbasePipelines.Classification);
        actualRun.Status.ShouldBe(PipelineRunStatus.Running);
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: Guid.NewGuid(),
            originalFileBlobName: $"blobs/{Guid.NewGuid():N}.pdf",
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}
