using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Duplicates;

[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class DuplicateScopeTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Both repositories are substituted so the diff algorithm can be driven directly. The two queries it
        // drives — the collision aggregate and the reconciliation projection — are asserted against the real
        // provider in EfCoreDocumentRepositoryDuplicate_Tests; what is under test here is what the job does with
        // their answers, and in particular which rows it never touches.
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// #651: the detection helper (§4) and the scope-reconciliation job (§5).
/// </summary>
public class DuplicateScope_Tests : VaultExtractApplicationTestBase<DuplicateScopeTestModule>
{
    private static readonly Guid TypeId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000651");
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-000000000651");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-000000000651");
    private const string Fingerprint = "fp-1";

    private readonly DuplicateDetectionEvaluator _evaluator;
    private readonly DuplicateScopeReconciliationJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDistributedEventBus _eventBus;

    public DuplicateScope_Tests()
    {
        _evaluator = GetRequiredService<DuplicateDetectionEvaluator>();
        _job = GetRequiredService<DuplicateScopeReconciliationJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    // --- §4 the detection helper -------------------------------------------

    [Fact]
    public async Task Evaluator_Asks_With_The_Types_Current_Scope_And_The_Documents_Own_Uploader()
    {
        var document = CreateFingerprintedDocument(creatorId: Alice);
        StubType(DuplicateDetectionScope.Uploader);
        StubCandidates();

        await _evaluator.EvaluateAsync(document);

        await _documentRepository.Received(1).FindDuplicateCandidatesAsync(
            document.Id,
            TypeId,
            Fingerprint,
            Arg.Any<int>(),
            DuplicateDetectionScope.Uploader,
            Alice,
            // #635 is orthogonal and unchanged: detection always counts against the whole layer's visibility.
            DocumentAccessScope.Unrestricted,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Evaluator_Falls_Back_To_Layer_When_The_Type_Cannot_Be_Resolved()
    {
        // Layer is the fail-safe direction: it is the wider of the two, so an unresolvable type can only
        // over-report into the review queue, never silently release a real duplicate downstream.
        var document = CreateFingerprintedDocument(creatorId: Alice);
        _documentTypeRepository.FindAsync(TypeId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((DocumentType?)null);
        StubCandidates();

        await _evaluator.EvaluateAsync(document);

        await _documentRepository.Received(1).FindDuplicateCandidatesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(),
            DuplicateDetectionScope.Layer, Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Evaluator_Reports_A_Collision_When_A_Candidate_Comes_Back()
    {
        var document = CreateFingerprintedDocument(creatorId: Alice);
        StubType(DuplicateDetectionScope.Uploader);
        StubCandidates(new DuplicateCandidateModel { Id = Guid.NewGuid(), Title = "Alice's other copy" });

        (await _evaluator.EvaluateAsync(document)).ShouldBeTrue();
    }

    [Fact]
    public async Task Evaluator_Never_ReFlags_An_Allowed_Document_And_Skips_The_Query()
    {
        var document = CreateFingerprintedDocument(creatorId: Alice);
        document.AllowDuplicate();
        StubType(DuplicateDetectionScope.Layer);
        StubCandidates(new DuplicateCandidateModel { Id = Guid.NewGuid() });

        (await _evaluator.EvaluateAsync(document)).ShouldBeFalse();
        await _documentRepository.DidNotReceive().FindDuplicateCandidatesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<DuplicateDetectionScope>(), Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Evaluator_Never_Flags_A_Document_With_No_Fingerprint_And_Skips_The_Query()
    {
        var document = CreateFingerprintedDocument(creatorId: Alice);
        document.SetFieldFingerprint(null);
        StubType(DuplicateDetectionScope.Layer);

        (await _evaluator.EvaluateAsync(document)).ShouldBeFalse();
        await _documentRepository.DidNotReceive().FindDuplicateCandidatesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<DuplicateDetectionScope>(), Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(),
            Arg.Any<CancellationToken>());
    }

    // --- §5 the reconciliation job -----------------------------------------

    /// <summary>
    /// Narrowing: the flagged half is the stale one. Alice's document was parked because Bob had a copy — under
    /// <c>Uploader</c> that is no longer a duplicate, and left alone it is a false park the uploader cannot
    /// resolve (the panel returns nothing for them, and Review has no owner arm).
    /// </summary>
    [Fact]
    public async Task Narrowing_Clears_A_Cross_Uploader_False_Park()
    {
        var alicesDoc = CreateFingerprintedDocument(creatorId: Alice);
        alicesDoc.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);

        StubType(DuplicateDetectionScope.Uploader);
        StubCollisions(
            (new DuplicateCollisionKey(Fingerprint, Alice), 1),
            (new DuplicateCollisionKey(Fingerprint, Bob), 1));
        StubProjection(RowOf(alicesDoc));
        StubLoad(alicesDoc);

        await _job.ExecuteAsync(NewArgs());

        (alicesDoc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.None);
        alicesDoc.DuplicateAllowed.ShouldBeFalse();   // cleared through SetReviewReason, never AllowDuplicate
        await _documentRepository.Received(1).UpdateAsync(alicesDoc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Widening: the unflagged half is the stale one. Two uploaders each held a copy and neither was flagged;
    /// layer-wide they collide.
    /// </summary>
    [Fact]
    public async Task Widening_Flags_A_Newly_Colliding_Pair()
    {
        var alicesDoc = CreateFingerprintedDocument(creatorId: Alice);
        var bobsDoc = CreateFingerprintedDocument(creatorId: Bob);

        StubType(DuplicateDetectionScope.Layer);
        StubCollisions((new DuplicateCollisionKey(Fingerprint, null), 2));
        StubProjection(RowOf(alicesDoc), RowOf(bobsDoc));
        StubLoad(alicesDoc, bobsDoc);

        await _job.ExecuteAsync(NewArgs());

        (alicesDoc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected)
            .ShouldBe(DocumentReviewReasons.DuplicateSuspected);
        (bobsDoc.ReviewReasons & DocumentReviewReasons.DuplicateSuspected)
            .ShouldBe(DocumentReviewReasons.DuplicateSuspected);
    }

    /// <summary>
    /// A recycle-bin row is absent from its own bucket, so a bucket of size 1 still means it would collide on
    /// restore — the asymmetry that makes the self-exclusion conditional. Its flag is corrected without a
    /// lifecycle re-derivation, and restoring it later lands on the right verdict.
    /// </summary>
    [Fact]
    public async Task RecycleBin_Row_Is_Flagged_Against_The_One_Live_Document_It_Would_Collide_With()
    {
        var binned = CreateFingerprintedDocument(creatorId: Alice);
        binned.IsDeleted = true;

        StubType(DuplicateDetectionScope.Layer);
        StubCollisions((new DuplicateCollisionKey(Fingerprint, null), 1));   // one LIVE document, not this one
        StubProjection(RowOf(binned));
        StubLoad(binned);

        await _job.ExecuteAsync(NewArgs());

        (binned.ReviewReasons & DocumentReviewReasons.DuplicateSuspected)
            .ShouldBe(DocumentReviewReasons.DuplicateSuspected);
    }

    [Fact]
    public async Task A_Live_Document_Alone_In_Its_Bucket_Is_Cleared()
    {
        // Bucket size 1 with the row itself live means the bucket IS the row: nothing else collides.
        var lonely = CreateFingerprintedDocument(creatorId: Alice);
        lonely.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);

        StubType(DuplicateDetectionScope.Layer);
        StubCollisions((new DuplicateCollisionKey(Fingerprint, null), 1));
        StubProjection(RowOf(lonely));
        StubLoad(lonely);

        await _job.ExecuteAsync(NewArgs());

        (lonely.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.None);
    }

    [Fact]
    public async Task DuplicateAllowed_Rows_Are_Never_Loaded_Or_Written()
    {
        // An operator decided. Reconciliation must neither re-litigate that nor forge one.
        var allowed = CreateFingerprintedDocument(creatorId: Alice);
        allowed.AllowDuplicate();

        StubType(DuplicateDetectionScope.Layer);
        StubCollisions((new DuplicateCollisionKey(Fingerprint, null), 2));   // would otherwise be flagged
        StubProjection(RowOf(allowed));
        StubLoad(allowed);

        await _job.ExecuteAsync(NewArgs());

        (allowed.ReviewReasons & DocumentReviewReasons.DuplicateSuspected).ShouldBe(DocumentReviewReasons.None);
        allowed.DuplicateAllowed.ShouldBeTrue();
        await _documentRepository.DidNotReceive().FindWithFieldValuesAsync(allowed.Id, Arg.Any<CancellationToken>());
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The point of steps 1-3: a document already carrying the verdict its new scope justifies is dropped from
    /// the narrow projection without ever being loaded. For a 10,000-document type with 200 verdicts changing,
    /// that is 200 row loads instead of 10,000.
    /// </summary>
    [Fact]
    public async Task Rows_Whose_Verdict_Does_Not_Change_Are_Never_Loaded()
    {
        var alreadyFlagged = CreateFingerprintedDocument(creatorId: Alice);
        alreadyFlagged.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);
        var alreadyClear = CreateFingerprintedDocument(creatorId: Bob, fingerprint: "fp-2");
        var noKeyAtAll = CreateFingerprintedDocument(creatorId: Bob, fingerprint: null);

        StubType(DuplicateDetectionScope.Layer);
        StubCollisions((new DuplicateCollisionKey(Fingerprint, null), 2));   // fp-2 is in no bucket
        StubProjection(RowOf(alreadyFlagged), RowOf(alreadyClear), RowOf(noKeyAtAll));
        StubLoad(alreadyFlagged, alreadyClear, noKeyAtAll);

        await _job.ExecuteAsync(NewArgs());

        await _documentRepository.DidNotReceive().FindWithFieldValuesAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The setting is read at execution time, never from the args — which is what makes two switches in quick
    /// succession converge on the later one instead of racing.
    /// </summary>
    [Fact]
    public async Task Reads_The_Types_Live_Setting_Rather_Than_Anything_In_The_Args()
    {
        StubType(DuplicateDetectionScope.Uploader);
        StubCollisions();
        // A page with something on it: an empty page asks the aggregate nothing, so it could not show which
        // scope the job resolved.
        StubProjection(RowOf(CreateFingerprintedDocument(creatorId: Alice)));

        await _job.ExecuteAsync(NewArgs());

        await _documentRepository.Received(1).CountDuplicateCollisionsAsync(
            TypeId, DuplicateDetectionScope.Uploader, Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The aggregate is bounded by the page, not by the type: it is asked about exactly the distinct non-null
    /// fingerprints on the page it is about to reconcile. Aggregating the whole type here instead would run one
    /// full-type aggregate per chained batch — quadratic in the type's size, against an issue whose case for
    /// reconciling on save is that it is cheap.
    /// </summary>
    [Fact]
    public async Task The_Aggregate_Is_Asked_For_Exactly_The_Pages_Distinct_Fingerprints()
    {
        var first = CreateFingerprintedDocument(creatorId: Alice);                              // fp-1
        var sameBucket = CreateFingerprintedDocument(creatorId: Bob);                           // fp-1 again
        var otherKey = CreateFingerprintedDocument(creatorId: Alice, fingerprint: "fp-2");
        var noKey = CreateFingerprintedDocument(creatorId: Alice, fingerprint: null);

        StubType(DuplicateDetectionScope.Layer);
        StubCollisions();
        StubProjection(RowOf(first), RowOf(sameBucket), RowOf(otherKey), RowOf(noKey));

        await _job.ExecuteAsync(NewArgs());

        // Distinct, and the keyless row contributes nothing.
        await _documentRepository.Received(1).CountDuplicateCollisionsAsync(
            TypeId,
            DuplicateDetectionScope.Layer,
            Arg.Is<IReadOnlyCollection<string>>(f =>
                f.Count == 2 && f.Contains(Fingerprint) && f.Contains("fp-2")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_Type_Is_Logged_And_Does_Nothing()
    {
        _documentTypeRepository.FindAsync(TypeId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((DocumentType?)null);

        await _job.ExecuteAsync(NewArgs());

        await _documentRepository.DidNotReceive().CountDuplicateCollisionsAsync(
            Arg.Any<Guid>(), Arg.Any<DuplicateDetectionScope>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publishes_Nothing_And_Spends_No_Llm_Call()
    {
        // Flag-only: reconciliation is not a re-extraction. It must not announce anything downstream, and in
        // particular must not re-fire DocumentReadyEto for a document whose lifecycle it re-derived.
        var doc = CreateFingerprintedDocument(creatorId: Alice);
        StubType(DuplicateDetectionScope.Layer);
        StubCollisions((new DuplicateCollisionKey(Fingerprint, null), 2));
        StubProjection(RowOf(doc));
        StubLoad(doc);

        await _job.ExecuteAsync(NewArgs());

        await _eventBus.DidNotReceiveWithAnyArgs().PublishAsync(
            Arg.Any<DocumentReadyEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    // --- helpers -----------------------------------------------------------

    private static DuplicateScopeReconciliationArgs NewArgs(Guid? afterId = null) =>
        new() { DocumentTypeId = TypeId, TenantId = null, AfterId = afterId };

    private void StubType(DuplicateDetectionScope scope)
    {
        var type = new DocumentType(TypeId, tenantId: null, "invoice", "Invoice", duplicateScope: scope);
        _documentTypeRepository.FindAsync(TypeId, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(type);
    }

    private void StubCandidates(params DuplicateCandidateModel[] candidates)
    {
        _documentRepository.FindDuplicateCandidatesAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<DuplicateDetectionScope>(), Arg.Any<Guid?>(), Arg.Any<DocumentAccessScope>(),
                Arg.Any<CancellationToken>())
            .Returns(candidates.ToList());
    }

    private void StubCollisions(params (DuplicateCollisionKey Key, int Count)[] buckets)
    {
        _documentRepository.CountDuplicateCollisionsAsync(
                Arg.Any<Guid>(), Arg.Any<DuplicateDetectionScope>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(buckets.ToDictionary(b => b.Key, b => b.Count));
    }

    private void StubProjection(params DuplicateReconciliationRow[] rows)
    {
        _documentRepository.GetDuplicateReconciliationPageAsync(
                Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(rows.ToList());
    }

    private void StubLoad(params Document[] documents)
    {
        foreach (var document in documents)
        {
            _documentRepository.FindWithFieldValuesAsync(document.Id, Arg.Any<CancellationToken>())
                .Returns(document);
        }
    }

    private static DuplicateReconciliationRow RowOf(Document document) =>
        new()
        {
            Id = document.Id,
            FieldFingerprint = document.FieldFingerprint,
            CreatorId = document.CreatorId,
            ReviewReasons = document.ReviewReasons,
            DuplicateAllowed = document.DuplicateAllowed,
            IsDeleted = document.IsDeleted
        };

    private static Document CreateFingerprintedDocument(Guid? creatorId, string? fingerprint = Fingerprint)
    {
        var document = new Document(
            Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        // Application.Tests has no InternalsVisibleTo on Domain, so the classified state and the ownership
        // anchor are written the way the sibling suites write them.
        typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, TypeId);
        if (creatorId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(document, creatorId.Value);
        }

        document.SetFieldFingerprint(fingerprint);
        return document;
    }
}
