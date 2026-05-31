using System;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Shouldly;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

public class DocumentPipelineRunAggregatePersistence_Tests
    : PaperbaseEntityFrameworkCoreTestBase
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentPipelineRunAggregatePersistence_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Aggregate_Update_Persists_New_Run_Appended_To_Loaded_Document()
    {
        var documentId = _guidGenerator.Create();
        Guid runId = default;

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(CreateDocument(documentId), autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
            var run = await _pipelineRunManager.QueueAsync(document, PaperbasePipelines.Classification);
            runId = run.Id;

            await _documentRepository.UpdateAsync(document, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
            var runs = document.PipelineRuns.Where(r => r.Id == runId).ToList();

            runs.Count.ShouldBe(1);
            runs[0].PipelineCode.ShouldBe(PaperbasePipelines.Classification);
            runs[0].Status.ShouldBe(PipelineRunStatus.Pending);
        });
    }

    private static Document CreateDocument(Guid id)
    {
        return new Document(
            id,
            tenantId: null,
            originalFileBlobName: "blobs/test.pdf",
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}
