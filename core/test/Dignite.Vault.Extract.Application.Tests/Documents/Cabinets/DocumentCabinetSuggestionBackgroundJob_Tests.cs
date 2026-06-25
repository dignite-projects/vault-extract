using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Cabinets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

[DependsOn(typeof(ExtractApplicationTestModule))]
public class DocumentCabinetSuggestionJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());

        var workflow = Substitute.ForPartsOf<CabinetSuggestionWorkflow>(
            Substitute.For<IChatClient>(),
            Options.Create(new ExtractBehaviorOptions()));
        context.Services.AddSingleton(workflow);

        context.Services.Configure<ExtractBehaviorOptions>(_ => { });
    }
}

/// <summary>
/// Behavior tests for <see cref="DocumentCabinetSuggestionBackgroundJob"/> (#265): manual-priority
/// gate, abstain / threshold, race recheck, fail-open, and LLM call outside UoW. IChatClient / workflow
/// are both replaced with NSubstitute, with no real LLM.
/// </summary>
public class DocumentCabinetSuggestionBackgroundJob_Tests
    : ExtractApplicationTestBase<DocumentCabinetSuggestionJobTestModule>
{
    private readonly DocumentCabinetSuggestionBackgroundJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly CabinetSuggestionWorkflow _workflow;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public DocumentCabinetSuggestionBackgroundJob_Tests()
    {
        _job = GetRequiredService<DocumentCabinetSuggestionBackgroundJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _workflow = GetRequiredService<CabinetSuggestionWorkflow>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
    }

    [Fact]
    public async Task Writes_CabinetId_When_Confident_Match()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "業務委託契約書の内容です。", cabinetId: null);
        SetupRepositories(doc, cabinet);

        StubWorkflow(new CabinetSuggestionOutcome { CabinetId = cabinet.Id, Confidence = 0.9 });

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBe(cabinet.Id);
        await _documentRepository.Received().UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_When_CabinetId_Already_Set_Manually()
    {
        var existing = Guid.NewGuid();
        var doc = CreateDocument(markdown: "anything", cabinetId: existing);
        SetupRepositories(doc, cabinet: null);

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBe(existing);
        // Manual priority: self-gate hits, so even the LLM is not called.
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_When_Markdown_Empty()
    {
        var doc = CreateDocument(markdown: null, cabinetId: null);
        SetupRepositories(doc, cabinet: null);

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_When_No_Cabinets_In_Layer()
    {
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet: null); // empty cabinet list

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_Not_Write_When_Workflow_Abstains()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        StubWorkflow(CabinetSuggestionOutcome.None);

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
    }

    [Fact]
    public async Task Does_Not_Write_When_Below_Confidence_Threshold()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        // Default threshold is 0.6; 0.4 should be rejected.
        StubWorkflow(new CabinetSuggestionOutcome { CabinetId = cabinet.Id, Confidence = 0.4 });

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
    }

    [Fact]
    public async Task FailsOpen_When_Workflow_Throws()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<CabinetSuggestionOutcome>(_ => throw new TimeoutException("LLM down"));

        // Fail-open: do not rethrow, and document remains unclassified.
        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
        // Assert workflow was actually called; otherwise CabinetId==null could be a false positive caused
        // by pre-gate short-circuiting, with catch never reached.
        await _workflow.Received(1).RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_Not_Overwrite_When_Operator_Reassigns_During_Llm()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var manualCabinetId = Guid.NewGuid();

        // Begin phase loads an unclassified document; by Complete phase reload, an operator has manually
        // reassigned it and CabinetId is already set.
        var docAtBegin = CreateDocument(markdown: "some text", cabinetId: null);
        var docAtComplete = CreateDocument(markdown: "some text", cabinetId: manualCabinetId, id: docAtBegin.Id);

        _documentRepository
            .GetAsync(docAtBegin.Id, false, Arg.Any<CancellationToken>())
            .Returns(docAtBegin, docAtComplete);
        _cabinetRepository
            .GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Cabinet> { cabinet });
        _cabinetRepository
            .FindAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(cabinet);

        StubWorkflow(new CabinetSuggestionOutcome { CabinetId = cabinet.Id, Confidence = 0.95 });

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = docAtBegin.Id });

        // AI must not overwrite the manually reassigned value.
        docAtComplete.CabinetId.ShouldBe(manualCabinetId);
        // Load-bearing: no document instance should be written back. This prevents a false positive if the
        // defensive recheck mistakenly calls SetCabinet on the docAtBegin instance.
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Swallows_Provider_Timeout_FailOpen_Without_Triggering_Retry()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        // This job is not a PipelineRun and is not retryable. Provider per-call timeout
        // (TaskCanceledException : OperationCanceledException) must be swallowed fail-open and never
        // escape ExecuteAsync; otherwise ABP treats it as failure and reschedules, causing retry storms.
        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<CabinetSuggestionOutcome>(_ => throw new TaskCanceledException("provider per-call timeout"));

        // Does not throw: swallowed. If catch incorrectly adds a
        // `when (ex is not OperationCanceledException)` filter, this call throws and the test fails.
        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
        await _workflow.Received(1).RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Llm_Call_Runs_Outside_Any_UnitOfWork()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        // background-jobs.md: external slow work (LLM) must execute outside UoW.
        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _unitOfWorkManager.Current.ShouldBeNull();
                return Task.FromResult(new CabinetSuggestionOutcome { CabinetId = cabinet.Id, Confidence = 0.9 });
            });

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBe(cabinet.Id);
    }

    private void StubWorkflow(CabinetSuggestionOutcome outcome)
    {
        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(outcome);
    }

    private void SetupRepositories(Document doc, Cabinet? cabinet)
    {
        _documentRepository
            .GetAsync(doc.Id, false, Arg.Any<CancellationToken>())
            .Returns(doc);

        _cabinetRepository
            .GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(cabinet == null ? new List<Cabinet>() : new List<Cabinet> { cabinet });

        if (cabinet != null)
        {
            _cabinetRepository
                .FindAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(cabinet);
        }
    }

    private static Document CreateDocument(string? markdown, Guid? cabinetId, Guid? id = null)
    {
        var doc = new Document(
            id ?? Guid.NewGuid(), null,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"),
            cabinetId: cabinetId);

        if (!string.IsNullOrEmpty(markdown))
        {
            typeof(Document)
                .GetProperty(nameof(Document.Markdown))!
                .GetSetMethod(nonPublic: true)!
                .Invoke(doc, [markdown]);
        }

        return doc;
    }
}
