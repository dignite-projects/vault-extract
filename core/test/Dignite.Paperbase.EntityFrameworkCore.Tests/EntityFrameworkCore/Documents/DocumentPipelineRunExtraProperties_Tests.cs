using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Shouldly;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

public class DocumentPipelineRunExtraProperties_Tests
    : PaperbaseEntityFrameworkCoreTestBase
{
    private readonly IRepository<Document, Guid> _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentPipelineRunExtraProperties_Tests()
    {
        _documentRepository = GetRequiredService<IRepository<Document, Guid>>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Should_RoundTrip_Classification_Candidates_In_ExtraProperties()
    {
        var documentId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            var document = CreateDocument(documentId);
            var run = await _pipelineRunManager.StartAsync(document, PaperbasePipelines.Classification);

            run.SetProperty(
                PipelineRunExtraPropertyNames.ClassificationCandidates,
                new List<PipelineRunCandidate>
                {
                    new("contract.general", 0.64),
                    new("invoice.standard", 0.31)
                });

            await _documentRepository.InsertAsync(document);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId);
            var run = document.GetLatestRun(PaperbasePipelines.Classification);

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
            originalFileBlobName: "blobs/test.pdf",
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}
