using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Pipelines;
using Shouldly;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

public class DocumentPipelineRunExtraProperties_Tests
    : ExtractEntityFrameworkCoreTestBase
{
    private readonly IRepository<Document, Guid> _documentRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentPipelineRunExtraProperties_Tests()
    {
        _documentRepository = GetRequiredService<IRepository<Document, Guid>>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Should_RoundTrip_Classification_Candidates_In_ExtraProperties()
    {
        var documentId = _guidGenerator.Create();
        Guid runId = default;

        // #216: after PipelineRun was split into an independent aggregate root, FK constraints require the
        // Document to be persisted before inserting a run. Use two UoWs: insert Document first, then start run.
        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(CreateDocument(documentId), autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: false);
            var run = await _pipelineRunManager.StartAsync(document, ExtractPipelines.Classification);
            runId = run.Id;

            run.SetProperty(
                PipelineRunExtraPropertyNames.ClassificationCandidates,
                new List<PipelineRunCandidate>
                {
                    new("contract.general", 0.64),
                    new("invoice.standard", 0.31)
                });
            // Manager.StartAsync has already called _runRepo.InsertAsync; the UoW commit flushes ExtraProperties
            // after SetProperty together with the run.
        });

        await WithUnitOfWorkAsync(async () =>
        {
            // Load by Id through runRepo, the independent aggregate-root read path.
            var run = await _runRepository.FindAsync(runId);

            run.ShouldNotBeNull();

            var candidates = run.GetProperty(PipelineRunExtraPropertyNames.ClassificationCandidates);

            var json = candidates.ShouldBeOfType<JsonElement>();
            json.ValueKind.ShouldBe(JsonValueKind.Array);
            json.GetArrayLength().ShouldBe(2);

            var first = json[0];
            first.GetProperty(nameof(PipelineRunCandidate.TypeCode)).GetString().ShouldBe("contract.general");
            first.GetProperty(nameof(PipelineRunCandidate.ConfidenceScore)).GetDouble().ShouldBe(0.64);

            var second = json[1];
            second.GetProperty(nameof(PipelineRunCandidate.TypeCode)).GetString().ShouldBe("invoice.standard");
            second.GetProperty(nameof(PipelineRunCandidate.ConfidenceScore)).GetDouble().ShouldBe(0.31);
        });
    }

    private static Document CreateDocument(Guid id)
    {
        return new Document(
            id,
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: "blobs/test.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}
